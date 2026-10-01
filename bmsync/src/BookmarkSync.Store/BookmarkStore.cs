using System.Text.Json;
using BookmarkSync.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BookmarkSync.Store;

/// <summary>
/// Holds all persisted state and serializes reads/writes of state.json.
/// </summary>
/// <remarks>
/// <para>
/// Concurrency model: single process with one mutex covering the whole
/// "read -&gt; merge -&gt; GC -&gt; write" critical section. No file locks: the service is a
/// single-container single process with no horizontal scaling (docs/architecture.md §6).
/// History snapshots are taken outside the critical section to keep hold time short.
/// </para>
/// <para>
/// A <see cref="SemaphoreSlim"/> instead of <c>lock</c>: <c>Sync</c> is async (it does
/// disk IO), and holding <c>lock</c> across an await lets "the same request's
/// continuation" resume on another thread and contend the same lock — the classic
/// ASP.NET deadlock. <c>SemaphoreSlim</c> + <c>await _gate.WaitAsync()</c> is the
/// async-correct equivalent.
/// </para>
/// </remarks>
public sealed class BookmarkStore : IDisposable
{
    private const string StateFileName = "state.json";
    private const string StateTmpFileName = "state.json.tmp";
    private const string ConflictFileName = "conflicts.json";
    private const string HistoryDirName = "history";
    private const int MaxConflictBuffer = 500;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _dir;
    private readonly StoreOptions _options;
    private readonly ILogger<BookmarkStore> _log;

    /// <summary>
    /// HLC persisted across requests. It must stay ahead of every client timestamp the
    /// server has seen so the hlc returned to clients stays authoritative. Recalibrated
    /// on restart by <see cref="LoadAsync"/> from the on-disk maximum.
    /// </summary>
    private readonly Hlc _clock = Hlc.New();

    private State _state = State.New();
    private List<Conflict> _conflicts = [];

    public BookmarkStore(StoreOptions options, ILogger<BookmarkStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _dir = options.DataDir;
        _options = options;
        _log = logger ?? NullLogger<BookmarkStore>.Instance;
    }

    /// <summary>Build a store and load on-disk state. Throws when the data dir is not writable or state is corrupt.</summary>
    public static async Task<BookmarkStore> CreateAsync(
        StoreOptions options, ILogger<BookmarkStore>? logger = null)
    {
        var store = new BookmarkStore(options, logger);
        store.EnsureDirs();
        await store.LoadAsync().ConfigureAwait(false);
        return store;
    }

    private void EnsureDirs()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(Path.Combine(_dir, HistoryDirName));
    }

    /// <summary>Release the internal semaphore.</summary>
    /// <remarks>
    /// Plain <c>IDisposable</c> is enough: <see cref="SemaphoreSlim"/>
    /// <c>Dispose</c> is synchronous with no async resources to drain. Implementing
    /// <c>IAsyncDisposable</c> would only lengthen every <c>await using</c> call site
    /// for zero benefit.
    /// </remarks>
    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// Read on-disk state. A first run (missing file) starts from an empty state.
    /// </summary>
    /// <remarks>
    /// A schema mismatch <b>refuses to start</b> instead of migrating: bookmarks are
    /// irreplaceable user data, and silent rewrites are riskier than one manual
    /// upgrade step (docs/architecture.md §6).
    /// </remarks>
    public async Task LoadAsync()
    {
        string path = Path.Combine(_dir, StateFileName);

        string? raw;
        try
        {
            raw = await File.ReadAllTextAsync(path).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            _log.LogInformation("No existing state found, starting empty {Path}", path);

            // Do not return early here. A missing state.json only means the state is
            // empty; the conflict buffer is a separate file that may already exist
            // (e.g. after syncing only tombstones). Returning early would make
            // conflicts.json permanently unreadable.
            try
            {
                await LoadConflictsAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to read conflicts, starting with empty buffer");
            }

            return;
        }

        State s;
        try
        {
            s = DomainJson.DeserializeState(raw)
                ?? throw new JsonException("parse result was null");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"failed to parse {path} (file may be corrupt): {ex.Message}", ex);
        }

        if (s.V != Schema.Version)
        {
            throw new InvalidDataException(
                $"{path} schema version is {s.V}, this service supports {Schema.Version}." +
                "Check that the bmsync version matches; the service refuses to start to avoid silently rewriting your bookmarks.");
        }

        ValidationResult validation = s.Validate();
        if (!validation.IsValid)
        {
            throw new InvalidDataException($"{path} failed validation: {validation.Error}");
        }

        _state = s;

        // Recalibrate the clock from the on-disk maximum after restart, otherwise the
        // service could emit timestamps smaller than history.
        _clock.Update(s.MaxHlc());

        _log.LogInformation("Loaded state with {Items} items ({Active} active)", s.Items.Count, s.CountActive());

        try
        {
            await LoadConflictsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to read conflicts, starting with empty buffer");
        }
    }

    /// <summary>
    /// The core serving path: merge, GC, persist.
    /// </summary>
    /// <remarks>
    /// The lock covers the whole flow because "read old state -&gt; merge -&gt; write new
    /// state" must be atomic. Two concurrent syncs reading the same old state would
    /// otherwise let the second write clobber the first and drop changes.
    /// </remarks>
    public async Task<MergeResult> SyncAsync(
        State incoming, IReadOnlyDictionary<string, string>? @base, string device)
    {
        ArgumentNullException.ThrowIfNull(incoming);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // The snapshot must be taken **before** writing the new state — it preserves
            // the pre-sync state, which is exactly the rollback point needed on failure.
            try
            {
                await SnapshotLockedAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A snapshot failure must not block sync: it is insurance, not a requirement.
                _log.LogWarning(ex, "Failed to create history snapshot (sync continues)");
            }

            MergeResult res = Merger.Merge(_state, incoming, @base, device, now);

            // GC reaps expired tombstones; the reaped count feeds the summary so users see
            // how many the server cleaned up.
            (State gcState, int removed) = TombstoneGc.CollectFrom(res.State, now, _options.TombstoneTtl);
            if (removed > 0)
            {
                res = new MergeResult
                {
                    State = gcState,
                    Conflicts = res.Conflicts,
                    Summary = res.Summary with { Deleted = res.Summary.Deleted + removed },
                };
            }

            // Advance the server clock past the largest timestamp seen.
            State ticked = res.State.WithClock(_clock.Update(res.State.MaxHlc()));

            ValidationResult validation = ticked.Validate();
            if (!validation.IsValid)
            {
                throw new InvalidOperationException($"merge result failed validation, sync aborted: {validation.Error}");
            }

            await SaveLockedAsync(ticked).ConfigureAwait(false);

            _state = ticked;
            if (res.Conflicts.Count > 0)
            {
                await AppendConflictsLockedAsync(res.Conflicts).ConfigureAwait(false);
            }

            return new MergeResult
            {
                State = ticked,
                Conflicts = res.Conflicts,
                Summary = res.Summary,
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Return a deep copy of current state (for read-only uses like /api/history).</summary>
    public async Task<State> GetStateAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return _state.Clone();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Return recent conflict records (latest limit entries).</summary>
    public async Task<IReadOnlyList<Conflict>> GetConflictsAsync(int limit)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (limit <= 0 || limit > _conflicts.Count)
            {
                limit = _conflicts.Count;
            }

            return _conflicts.Skip(_conflicts.Count - limit).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<int> GetItemCountAsync() => CountAsync(static s => s.Items.Count);

    public Task<int> GetActiveCountAsync() => CountAsync(static s => s.CountActive());

    private async Task<int> CountAsync(Func<State, int> selector)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return selector(_state);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>List history snapshots (read-only; rollback is manual, see ops docs).</summary>
    public async Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            string dir = Path.Combine(_dir, HistoryDirName);
            if (!Directory.Exists(dir))
            {
                return [];
            }

            var snapshots = new List<SnapshotInfo>();
            foreach (string file in Directory.EnumerateFiles(dir, "*.json"))
            {
                FileInfo fi = new(file);
                int items = 0;
                try
                {
                    string raw = await File.ReadAllTextAsync(file).ConfigureAwait(false);
                    State? s = DomainJson.DeserializeState(raw);
                    items = s?.Items.Count ?? 0;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Failed to read snapshot {File}, recording items as 0", file);
                }

                snapshots.Add(new SnapshotInfo
                {
                    Id = Path.GetFileNameWithoutExtension(file),
                    // LastWriteTimeUtc is DateTime (Kind=Utc), not DateTimeOffset —
                    // the two cannot mix directly, so wrap explicitly.
                    At = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                    Items = items,
                    Size = fi.Length,
                });
            }

            snapshots.Sort((a, b) => b.At.CompareTo(a.At));
            return snapshots;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── Persistence ───────────────────────────────────────────────────────

    /// <summary>
    /// Atomically write state.json.
    /// </summary>
    /// <remarks>
    /// Atomicity comes from rename(2): POSIX guarantees that renaming a new file over
    /// an existing path leaves readers with either the full old or the full new
    /// contents, never a torn state. That property is the sole basis for "data stays
    /// parseable after kill -9" (docs/architecture.md §6).
    /// <para>Callers must hold <see cref="_gate"/>.</para>
    /// </remarks>
    private async Task SaveLockedAsync(State s)
    {
        string final = Path.Combine(_dir, StateFileName);
        string tmp = Path.Combine(_dir, StateTmpFileName);

        byte[] raw = DomainJson.SerializeToUtf8Bytes(s);
        await File.WriteAllBytesAsync(tmp, raw).ConfigureAwait(false);

        // Flush buffers to disk, otherwise rename may win ahead of the data itself.
        // Writing via FileStream + Flush(true) saves one open vs "write then reopen to
        // fsync" with identical semantics.
        await using (var fs = new FileStream(tmp, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            fs.Flush(flushToDisk: true);
        }

        if (!FileOps.RenameWithRetry(tmp, final, attempts: 5, out Exception? err))
        {
            throw new IOException($"failed to atomically replace {final}: {err?.Message}", err);
        }

        // Directory fsync: durability insurance so the rename itself hits disk.
        FileOps.TrySyncDir(Path.GetDirectoryName(final)!);
    }

    /// <summary>
    /// Copy the current state into history/. Timestamp-based names sort chronologically
    /// and stay human-readable. Callers must hold <see cref="_gate"/>.
    /// </summary>
    private async Task SnapshotLockedAsync()
    {
        if (_state.Items.Count == 0)
        {
            return; // Nothing worth backing up in an empty state.
        }

        string dir = Path.Combine(_dir, HistoryDirName);
        string name = DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyyMMddTHHmmss.fffZ", null) + ".json";

        byte[] raw = DomainJson.SerializeToUtf8Bytes(_state);
        await File.WriteAllBytesAsync(Path.Combine(dir, name), raw).ConfigureAwait(false);

        PruneSnapshotsLocked(dir);
    }

    private void PruneSnapshotsLocked(string dir)
    {
        var names = Directory.EnumerateFiles(dir, "*.json")
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        if (names.Count <= _options.HistoryKeep)
        {
            return;
        }

        // Filenames start with a timestamp, so lexicographic order == time order;
        // delete oldest first after sorting by name.
        foreach (string n in names.Take(names.Count - _options.HistoryKeep))
        {
            try
            {
                File.Delete(Path.Combine(dir, n));
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to delete old snapshot {File}", n);
            }
        }
    }

    // ── Conflict buffer ───────────────────────────────────────────────────

    private string ConflictPath => Path.Combine(_dir, ConflictFileName);

    private async Task LoadConflictsAsync()
    {
        string raw;
        try
        {
            raw = await File.ReadAllTextAsync(ConflictPath).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            _conflicts = [];
            return;
        }

        _conflicts = DomainJson.DeserializeConflicts(raw) ?? [];
    }

    private async Task AppendConflictsLockedAsync(IReadOnlyList<Conflict> list)
    {
        _conflicts.AddRange(list);
        if (_conflicts.Count > MaxConflictBuffer)
        {
            _conflicts = _conflicts.Skip(_conflicts.Count - MaxConflictBuffer).ToList();
        }

        try
        {
            byte[] raw = DomainJson.SerializeToUtf8Bytes(_conflicts);
            await File.WriteAllBytesAsync(ConflictPath, raw).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Conflict logs are purely observational; failing to write them must not fail sync.
            _log.LogWarning(ex, "Failed to write conflicts");
        }
    }
}
