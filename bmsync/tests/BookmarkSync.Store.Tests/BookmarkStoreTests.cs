using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookmarkSync.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace BookmarkSync.Store.Tests;

/// <summary>
/// Test scaffolding for the store package.
/// </summary>
/// <remarks>
/// Duplicating the same-named file under BookmarkSync.Domain.Tests is deliberate: C# cannot share
/// test base classes across projects, and the two sides need different key derivation (a store
/// prefix here, to avoid confusion with domain-layer cases).
/// </remarks>
internal static class Fx
{
    public const string DeviceA = "device-a";
    public const string DeviceB = "device-b";

    /// <summary>Fixed wall-clock baseline so assertions involving x (deletion time) are deterministic.</summary>
    public const long T0 = 1_700_000_000_000L;

    public static string KeyOf(string label)
    {
        byte[] sum = SHA256.HashData(Encoding.UTF8.GetBytes("bmsync-fixture:" + label));
        return Convert.ToHexStringLower(sum.AsSpan(0, 16));
    }

    public static Item ValidBookmark(string parent) => new()
    {
        P = parent, T = ItemTypes.Bookmark, N = "Title", U = "https://example.com",
        M = Hlc.Encode(100, 0), A = Hlc.Encode(100, 0),
    };

    public static Item ValidFolder(string parent) => new()
    {
        P = parent, T = ItemTypes.Folder, N = "Folder",
        M = Hlc.Encode(100, 0), A = Hlc.Encode(100, 0),
    };

    public static State StateOf(params (string Label, Item Item)[] items)
    {
        State s = State.New();
        foreach ((string label, Item it) in items)
        {
            s.Items[KeyOf(label)] = it;
        }

        return s;
    }

    public static bool SameStates(State a, State b)
    {
        if (a.Items.Count != b.Items.Count)
        {
            return false;
        }

        foreach ((string k, Item va) in a.Items)
        {
            if (!b.Items.TryGetValue(k, out Item vb) || va != vb)
            {
                return false;
            }
        }

        return true;
    }

    public static StoreOptions Options(string? dataDir = null)
    {
        string dir = dataDir
                     ?? Path.Combine(Path.GetTempPath(), "bmsync-test-" + Guid.NewGuid().ToString("N"));

        // The directory must be created first. Go relies on t.TempDir() creating it implicitly;
        // C# has no equivalent — forgetting it surfaces as DirectoryNotFoundException instead of
        // "directory missing", with nothing in the message pointing at the real cause.
        Directory.CreateDirectory(dir);

        return new StoreOptions
        {
            DataDir = dir,
            HistoryKeep = 3,
            TombstoneTtl = TimeSpan.FromDays(90),
        };
    }

    public static Task<BookmarkStore> NewStoreAsync(StoreOptions options) =>
        BookmarkStore.CreateAsync(options, NullLogger<BookmarkStore>.Instance);
}

/// <summary>
/// Unit tests for the persistence layer, mirroring the persistence test cases.
/// </summary>
public class BookmarkStoreTests
{
    [Fact]
    public async Task StartsEmptyInEmptyDirectory()
    {
        StoreOptions options = Fx.Options();
        using BookmarkStore store = await Fx.NewStoreAsync(options);

        Assert.Empty((await store.GetStateAsync()).Items);
        Assert.False(File.Exists(Path.Combine(options.DataDir, "state.json")),
            "a fresh directory must not already contain state.json");
    }

    [Fact]
    public async Task RejectsSchemaVersionMismatch()
    {
        StoreOptions options = Fx.Options();
        await File.WriteAllTextAsync(
            Path.Combine(options.DataDir, "state.json"), """{"v":99,"items":{}}""");

        InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => Fx.NewStoreAsync(options));
        Assert.Contains("schema", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Auto-migration looks "convenient", but for irreplaceable user data the risk far outweighs the benefit.</summary>
    [Fact]
    public async Task RejectsCorruptFile()
    {
        StoreOptions options = Fx.Options();
        await File.WriteAllTextAsync(Path.Combine(options.DataDir, "state.json"), "{not json");

        await Assert.ThrowsAsync<InvalidDataException>(() => Fx.NewStoreAsync(options));
    }

    [Fact]
    public async Task RestoresSavedState()
    {
        StoreOptions options = Fx.Options();
        State saved = Fx.StateOf(("b", Fx.ValidBookmark(RootFolders.Toolbar)));
        await File.WriteAllTextAsync(
            Path.Combine(options.DataDir, "state.json"),
            JsonSerializer.Serialize(saved, BmsyncJson.Storage));

        using BookmarkStore store = await Fx.NewStoreAsync(options);
        Assert.Single((await store.GetStateAsync()).Items);
    }

    [Fact]
    public async Task PersistsAfterSyncAndReloads()
    {
        StoreOptions options = Fx.Options();
        using BookmarkStore store = await Fx.NewStoreAsync(options);

        await store.SyncAsync(Fx.StateOf(("b", Fx.ValidBookmark(RootFolders.Toolbar))), null, Fx.DeviceA);

        string raw = await File.ReadAllTextAsync(Path.Combine(options.DataDir, "state.json"));
        var reloaded = JsonSerializer.Deserialize<State>(raw, BmsyncJson.Storage)!;
        Assert.Single(reloaded.Items);

        // No temp file may be left behind
        Assert.False(File.Exists(Path.Combine(options.DataDir, "state.json.tmp")), "temp file was not cleaned up");
    }

    [Fact]
    public async Task RejectsInvalidResultWithoutTouchingPersistedState()
    {
        StoreOptions options = Fx.Options();
        using BookmarkStore store = await Fx.NewStoreAsync(options);
        await store.SyncAsync(Fx.StateOf(("b", Fx.ValidBookmark(RootFolders.Toolbar))), null, Fx.DeviceA);

        string path = Path.Combine(options.DataDir, "state.json");
        string before = await File.ReadAllTextAsync(path);

        // A state with depth over the limit
        State deep = State.New();
        string parent = RootFolders.Toolbar;
        for (int i = 0; i <= Limits.MaxDepth + 3; i++)
        {
            string k = Fx.KeyOf($"deep{(char)('a' + (i % 26))}{(char)('a' + (i / 26))}");
            deep.Items[k] = Fx.ValidFolder(parent);
            parent = k;
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SyncAsync(deep, null, Fx.DeviceB));

        Assert.Equal(before, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task SnapshotsBeforeSync()
    {
        StoreOptions options = Fx.Options();
        using BookmarkStore store = await Fx.NewStoreAsync(options);

        // First sync: empty state, no snapshot produced
        await store.SyncAsync(Fx.StateOf(("b1", Fx.ValidBookmark(RootFolders.Toolbar))), null, Fx.DeviceA);

        // Second sync: should produce one "pre-sync" snapshot
        await store.SyncAsync(Fx.StateOf(
            ("b1", Fx.ValidBookmark(RootFolders.Toolbar)),
            ("b2", Fx.ValidBookmark(RootFolders.Unfiled))), null, Fx.DeviceA);

        IReadOnlyList<SnapshotInfo> snaps = await store.ListSnapshotsAsync();
        SnapshotInfo snap = Assert.Single(snaps);

        // Snapshot content should be the "pre-sync" state: b1 only, no b2
        string raw = await File.ReadAllTextAsync(
            Path.Combine(options.DataDir, "history", snap.Id + ".json"));
        var content = JsonSerializer.Deserialize<State>(raw, BmsyncJson.Storage)!;

        Assert.Single(content.Items);
        Assert.Contains(Fx.KeyOf("b1"), content.Items.Keys);
    }

    [Fact]
    public async Task PrunesSnapshotsBeyondRetention()
    {
        StoreOptions options = Fx.Options(); // HistoryKeep = 3
        using BookmarkStore store = await Fx.NewStoreAsync(options);
        State baseline = Fx.StateOf(("b1", Fx.ValidBookmark(RootFolders.Toolbar)));

        for (int i = 0; i < 8; i++)
        {
            await store.SyncAsync(baseline, null, Fx.DeviceA);
            await Task.Delay(2); // Snapshot names are millisecond timestamps
        }

        int count = Directory.GetFiles(Path.Combine(options.DataDir, "history"), "*.json").Length;
        Assert.True(count <= options.HistoryKeep, $"snapshot count {count} exceeds retention {options.HistoryKeep}");
    }

    /// <summary>
    /// The lock covers the whole "read → merge → write", so concurrent syncs lose no updates.
    /// After 100 concurrent writes, all 100 bookmarks must be present.
    /// </summary>
    [Fact]
    public async Task ConcurrentSyncLosesNoUpdates()
    {
        StoreOptions options = Fx.Options();
        using BookmarkStore store = await Fx.NewStoreAsync(options);

        const int n = 100;
        var errors = new List<Exception>();

        await Parallel.ForEachAsync(Enumerable.Range(0, n), async (i, _) =>
        {
            try
            {
                Item item = Fx.ValidBookmark(RootFolders.Toolbar) with
                {
                    N = $"bookmark-{(char)('a' + (i % 26))}{(char)('a' + ((i / 26) % 26))}-{i}",
                    U = $"https://e.example/{i}",
                };
                State incoming = Fx.StateOf(("concurrent" + i, item));
                await store.SyncAsync(incoming, null, "dev-" + i);
            }
            catch (Exception ex)
            {
                lock (errors)
                {
                    errors.Add(ex);
                }
            }
        });

        Assert.Empty(errors);

        Assert.Equal(n, await store.GetItemCountAsync());

        // The persisted file must be complete too
        string raw = await File.ReadAllTextAsync(Path.Combine(options.DataDir, "state.json"));
        var reloaded = JsonSerializer.Deserialize<State>(raw, BmsyncJson.Storage)!;
        Assert.Equal(n, reloaded.Items.Count);
    }

    [Fact]
    public async Task RepeatedSyncIsIdempotent()
    {
        using BookmarkStore store = await Fx.NewStoreAsync(Fx.Options());
        State incoming = Fx.StateOf(("b", Fx.ValidBookmark(RootFolders.Toolbar)));

        MergeResult first = await store.SyncAsync(incoming, null, Fx.DeviceA);
        for (int i = 0; i < 4; i++)
        {
            MergeResult again = await store.SyncAsync(incoming, null, Fx.DeviceA);
            Assert.True(Fx.SameStates(first.State, again.State), $"repeat sync #{i + 1} diverged");
            Assert.Equal(0, again.Summary.Created);
        }
    }

    [Fact]
    public async Task ConflictRingBuffer()
    {
        using BookmarkStore store = await Fx.NewStoreAsync(Fx.Options());
        Item orig = Fx.ValidBookmark(RootFolders.Toolbar);
        string k = Fx.KeyOf("b");
        var baseMap = new Dictionary<string, string>(StringComparer.Ordinal) { [k] = orig.M };

        await store.SyncAsync(Fx.StateOf(("b", orig)), baseMap, Fx.DeviceA);

        // Produce several genuine concurrent edits.
        //
        // Key point: "the client changed it" alone is not a conflict — base records what both sides
        // last saw, so only **both sides changing the same item off the same baseline** counts.
        // Hence each round below changes the server side first.
        for (int round = 0; round < 10; round++)
        {
            Item serverSide = Fx.ValidBookmark(RootFolders.Toolbar) with
            {
                N = "Cloud edit " + round,
                M = Hlc.Encode(2000 + (round * 2), 0),
            };
            await store.SyncAsync(Fx.StateOf(("b", serverSide)), baseMap, Fx.DeviceA);

            Item clientSide = Fx.ValidBookmark(RootFolders.Toolbar) with
            {
                N = "Local edit " + round,
                M = Hlc.Encode(2001 + (round * 2), 0),
            };

            int before = (await store.GetConflictsAsync(0)).Count;
            await store.SyncAsync(Fx.StateOf(("b", clientSide)), baseMap, Fx.DeviceB);

            int after = (await store.GetConflictsAsync(0)).Count;
            Assert.True(after > before, $"round {round} should record a conflict");
        }

        IReadOnlyList<Conflict> all = await store.GetConflictsAsync(0);
        Assert.True(all.Count <= 500, $"conflict buffer holds {all.Count}, over the 500 cap");
        Assert.Equal(3, (await store.GetConflictsAsync(3)).Count);
    }

    [Fact]
    public async Task ConflictBufferSurvivesRestart()
    {
        StoreOptions options = Fx.Options();
        using (BookmarkStore st = await Fx.NewStoreAsync(options))
        {
            // Produce the conflict via a real sync — closer to the live path than calling internals
            Item orig = Fx.ValidBookmark(RootFolders.Toolbar);
            string k = Fx.KeyOf("b");
            var baseMap = new Dictionary<string, string>(StringComparer.Ordinal) { [k] = orig.M };
            await st.SyncAsync(Fx.StateOf(("b", orig)), baseMap, Fx.DeviceA);

            await st.SyncAsync(
                Fx.StateOf(("b", orig with { N = "Cloud", M = Hlc.Encode(300, 0) })), baseMap, Fx.DeviceA);
            await st.SyncAsync(
                Fx.StateOf(("b", orig with { N = "Local", M = Hlc.Encode(200, 0) })), baseMap, Fx.DeviceB);
        }

        using BookmarkStore reopened = await Fx.NewStoreAsync(options);
        Assert.NotEmpty(await reopened.GetConflictsAsync(0));
    }

    [Fact]
    public async Task GetStateReturnsDeepCopy()
    {
        using BookmarkStore store = await Fx.NewStoreAsync(Fx.Options());
        await store.SyncAsync(Fx.StateOf(("b", Fx.ValidBookmark(RootFolders.Toolbar))), null, Fx.DeviceA);

        State got = await store.GetStateAsync();
        got.Items.Remove(Fx.KeyOf("b"));

        Assert.Equal(1, await store.GetItemCountAsync());
    }

    [Fact]
    public async Task EmptyDirectoryListsNoSnapshots()
    {
        using BookmarkStore store = await Fx.NewStoreAsync(Fx.Options());
        Assert.Empty(await store.ListSnapshotsAsync());
    }

    [Fact]
    public async Task ActiveCountCountsOnlyLiveItems()
    {
        using BookmarkStore store = await Fx.NewStoreAsync(Fx.Options());

        // The tombstone x uses the **current** time: with Fx.T0 (2023) it predates the 90-day TTL,
        // would be GC-reaped in the same Sync, and the measured ItemCount would become 1 —
        // looking like "merge dropped an item" when really the test data expired.
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        State incoming = Fx.StateOf(
            ("live", Fx.ValidBookmark(RootFolders.Toolbar)),
            ("dead", Fx.ValidBookmark(RootFolders.Unfiled) with { D = true, X = now, M = Hlc.Encode(200, 0) }));

        await store.SyncAsync(incoming, null, Fx.DeviceA);

        Assert.Equal(2, await store.GetItemCountAsync());
        Assert.Equal(1, await store.GetActiveCountAsync());
    }

    [Fact]
    public async Task ExpiredTombstonesAreGcCollected()
    {
        // Mirror of the previous test: confirms GC really works, not just "nothing deleted because nothing expired".
        using BookmarkStore store = await Fx.NewStoreAsync(Fx.Options());

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long longAgo = now - (100 * 24 * 60 * 60 * 1000L);

        State incoming = Fx.StateOf(
            ("live", Fx.ValidBookmark(RootFolders.Toolbar)),
            ("dead", Fx.ValidBookmark(RootFolders.Unfiled) with { D = true, X = longAgo, M = Hlc.Encode(200, 0) }));

        MergeResult res = await store.SyncAsync(incoming, null, Fx.DeviceA);

        Assert.Equal(1, await store.GetItemCountAsync());
        Assert.Equal(1, res.Summary.Deleted);
    }
}
