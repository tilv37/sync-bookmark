using System.Text.Json;
using BookmarkSync.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BookmarkSync.Store;

/// <summary>
/// 持有全部持久化状态，并串行化对 state.json 的读写。
/// </summary>
/// <remarks>
/// <para>
/// 并发模型：单进程 + 一个互斥锁覆盖整个「读 → 合并 → GC → 写」临界区。
/// 不用文件锁，因为服务是单容器单进程，没有横向扩展需求（design.md §8.4）。
/// 临界区里不写历史快照 —— 快照在临界区外做，避免拖长锁持有时间。
/// </para>
/// <para>
/// 锁用 <see cref="SemaphoreSlim"/> 而不是 <c>lock</c>：<c>Sync</c> 是
/// async 的（内部有落盘 IO），在 async 方法里持有 <c>lock</c> 会让
/// "同一请求的续体"在别的线程上跑，从而可能与另一个请求争同一把锁 ——
/// 那是经典的 ASP.NET 死锁。语义化的替代是 <c>SemaphoreSlim</c> +
/// <c>await _gate.WaitAsync()</c>。
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
    /// 跨请求持久的 HLC。它必须比服务端见过的所有客户端时间戳都大，
    /// 这样返回给客户端的 hlc 才是权威的。进程重启后由 <see cref="Load"/>
    /// 用 state 里的最大值重新校准。
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

    /// <summary>构造并加载磁盘上的状态。数据目录不可写或 state 损坏时抛异常。</summary>
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

    /// <summary>释放内部的信号量。</summary>
    /// <remarks>
    /// 不用 <c>IDisposable</c> 之外的实现：<see cref="SemaphoreSlim"/> 的
    /// <c>Dispose</c> 是同步的，且没有需要等待的异步资源。注册成
    /// <c>IAsyncDisposable</c> 只会让每个调用点的 <c>await using</c> 变长，
    /// 而没有任何收益。
    /// </remarks>
    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// 读取磁盘上的状态。首次运行（文件不存在）时初始化一个空 state。
    /// </summary>
    /// <remarks>
    /// schema 版本不匹配时<b>拒绝启动</b>而不是自动迁移：书签是用户不可再生的
    /// 数据，静默改写的风险远大于"升级后手工处理一次"（design.md §8.7）。
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
            _log.LogInformation("未发现已有状态，从空状态开始 {Path}", path);

            // 注意：这里不能直接 return。state.json 不存在只说明状态是空的，
            // 冲突缓冲是独立的文件，可能已经存在（比如只同步过墓碑的极端情况）。
            // 早退会让 conflicts.json 永远读不回来。
            try
            {
                await LoadConflictsAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "读取冲突记录失败，将从空缓冲开始");
            }

            return;
        }

        State s;
        try
        {
            s = DomainJson.DeserializeState(raw)
                ?? throw new JsonException("解析结果为 null");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"解析 {path} 失败（文件可能已损坏）: {ex.Message}", ex);
        }

        if (s.V != Schema.Version)
        {
            throw new InvalidDataException(
                $"{path} 的 schema 版本是 {s.V}，本服务只支持 {Schema.Version}。" +
                "请确认 bmsync 版本是否匹配；为避免静默改写你的书签，服务拒绝启动。");
        }

        ValidationResult validation = s.Validate();
        if (!validation.IsValid)
        {
            throw new InvalidDataException($"{path} 未通过校验: {validation.Error}");
        }

        _state = s;

        // 重启后用磁盘上的最大值重新校准时钟，否则服务可能发出比历史记录更小的时间戳
        _clock.Update(s.MaxHlc());

        _log.LogInformation("已载入状态 {Items} 项（活跃 {Active}）", s.Items.Count, s.CountActive());

        try
        {
            await LoadConflictsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "读取冲突记录失败，将从空缓冲开始");
        }
    }

    /// <summary>
    /// 服务的核心路径：合并、GC、落盘。
    /// </summary>
    /// <remarks>
    /// 锁覆盖整个过程，因为「读旧状态 → 合并 → 写新状态」必须是一个原子步骤。
    /// 两次并发同步如果各自读到了同一份旧状态，后写入的那次会覆盖前一次的结果，
    /// 丢掉中间的改动。
    /// </remarks>
    public async Task<MergeResult> SyncAsync(
        State incoming, IReadOnlyDictionary<string, string>? @base, string device)
    {
        ArgumentNullException.ThrowIfNull(incoming);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // 快照必须在写新状态**之前**完成 —— 它保存的是"这次同步发生前"的
            // 状态，正是出问题时需要回滚到的那个点。
            try
            {
                await SnapshotLockedAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 快照失败不该阻断同步：它只是保险，不是必需品。
                _log.LogWarning(ex, "创建历史快照失败（同步继续）");
            }

            MergeResult res = Merger.Merge(_state, incoming, @base, device, now);

            // GC 清理过期墓碑，清理量计入 summary，用户能看到"服务端清理了多少"
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

            // 服务端时钟追平它见过的最大时间戳
            State ticked = res.State.WithClock(_clock.Update(res.State.MaxHlc()));

            ValidationResult validation = ticked.Validate();
            if (!validation.IsValid)
            {
                throw new InvalidOperationException($"合并结果未通过校验，已放弃本次同步: {validation.Error}");
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

    /// <summary>返回当前状态的深拷贝（供 /api/history 之类只读用途）。</summary>
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

    /// <summary>返回最近的冲突记录（最近 limit 条）。</summary>
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

    /// <summary>列出历史快照（只读，不提供回滚 —— 回滚是手工操作，见运维文档）。</summary>
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

            // 注意变量名不能叫 out —— 那是 C# 的保留字（out 参数修饰符）。
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
                    _log.LogWarning(ex, "读取快照 {File} 失败，items 记为 0", file);
                }

                snapshots.Add(new SnapshotInfo
                {
                    Id = Path.GetFileNameWithoutExtension(file),
                    // LastWriteTimeUtc 是 DateTime（Kind=Utc），不是 DateTimeOffset，
                    // 两者不能直接混用 —— 这里显式包一层。
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

    // ── 落盘 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 原子写入 state.json。
    /// </summary>
    /// <remarks>
    /// 原子性来自 rename(2)：POSIX 保证把新文件 rename 到已存在的路径上时，
    /// 其他进程要么看到完整旧内容，要么看到完整新内容，不存在中间态。
    /// 这条性质是"kill -9 之后数据仍可解析"的唯一依据（design.md §8.3）。
    /// <para>调用方必须持有 <see cref="_gate"/>。</para>
    /// </remarks>
    private async Task SaveLockedAsync(State s)
    {
        string final = Path.Combine(_dir, StateFileName);
        string tmp = Path.Combine(_dir, StateTmpFileName);

        byte[] raw = DomainJson.SerializeToUtf8Bytes(s);
        await File.WriteAllBytesAsync(tmp, raw).ConfigureAwait(false);

        // 落盘缓冲，否则 rename 可能先于数据真正写入磁盘。
        // 直接写 FileStream 并 Flush(true) 比"写完再另开一个句柄 fsync"少一次打开，
        // 且语义相同。
        await using (var fs = new FileStream(tmp, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            fs.Flush(flushToDisk: true);
        }

        if (!FileOps.RenameWithRetry(tmp, final, attempts: 5, out Exception? err))
        {
            throw new IOException($"原子替换 {final} 失败: {err?.Message}", err);
        }

        // 目录 fsync 是"重命名本身也要落盘"的耐久化保险。
        FileOps.TrySyncDir(Path.GetDirectoryName(final)!);
    }

    /// <summary>
    /// 把当前 state 复制到 history/。命名用时间戳而不是随机数：天然按时间排序，
    /// 且人眼可读。调用方必须持有 <see cref="_gate"/>。
    /// </summary>
    private async Task SnapshotLockedAsync()
    {
        if (_state.Items.Count == 0)
        {
            return; // 空状态没什么可备份的
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

        // 文件名以时间戳开头，字典序 == 时间序，所以按名字排序后从最旧的开始删
        foreach (string n in names.Take(names.Count - _options.HistoryKeep))
        {
            try
            {
                File.Delete(Path.Combine(dir, n));
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "删除旧快照失败 {File}", n);
            }
        }
    }

    // ── 冲突缓冲 ──────────────────────────────────────────────────────────

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
            // 冲突日志是纯观测品，写不进去不该让同步失败
            _log.LogWarning(ex, "写入冲突记录失败");
        }
    }
}
