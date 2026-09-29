using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookmarkSync.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace BookmarkSync.Store.Tests;

/// <summary>
/// store 包的测试脚手架。
/// </summary>
/// <remarks>
/// 与 BookmarkSync.Domain.Tests 下同名文件重复是有意的：C# 不支持跨项目共享
/// 测试基类，而两边需要的 key 生成方式也不同（这里用 store 前缀，避免与
/// 领域层用例混淆）。
/// </remarks>
internal static class Fx
{
    public const string DeviceA = "device-a";
    public const string DeviceB = "device-b";

    /// <summary>固定墙钟基准，让涉及 x（删除时间）的断言确定。</summary>
    public const long T0 = 1_700_000_000_000L;

    public static string KeyOf(string label)
    {
        byte[] sum = SHA256.HashData(Encoding.UTF8.GetBytes("bmsync-fixture:" + label));
        return Convert.ToHexStringLower(sum.AsSpan(0, 16));
    }

    public static Item ValidBookmark(string parent) => new()
    {
        P = parent, T = ItemTypes.Bookmark, N = "标题", U = "https://example.com",
        M = Hlc.Encode(100, 0), A = Hlc.Encode(100, 0),
    };

    public static Item ValidFolder(string parent) => new()
    {
        P = parent, T = ItemTypes.Folder, N = "目录",
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

        // 必须先把目录建出来。Go 版靠 t.TempDir() 隐式建目录，C# 没有等价物 ——
        // 忘了建的话，症状是 DirectoryNotFoundException 而不是"目录不存在"，
        // 报错信息里没有任何一句提到真正的原因。
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
/// 持久化层的单元测试，逐条对应
/// 持久化层的测试用例（同上）。
/// </summary>
public class BookmarkStoreTests
{
    [Fact]
    public async Task 空目录下从空状态开始()
    {
        StoreOptions options = Fx.Options();
        using BookmarkStore store = await Fx.NewStoreAsync(options);

        Assert.Empty((await store.GetStateAsync()).Items);
        Assert.False(File.Exists(Path.Combine(options.DataDir, "state.json")),
            "全新目录不应已有 state.json");
    }

    [Fact]
    public async Task 拒绝schema版本不符()
    {
        StoreOptions options = Fx.Options();
        await File.WriteAllTextAsync(
            Path.Combine(options.DataDir, "state.json"), """{"v":99,"items":{}}""");

        InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => Fx.NewStoreAsync(options));
        Assert.Contains("schema", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>自动迁移看起来"方便"，但对不可再生的用户数据来说风险远大于收益。</summary>
    [Fact]
    public async Task 拒绝损坏文件()
    {
        StoreOptions options = Fx.Options();
        await File.WriteAllTextAsync(Path.Combine(options.DataDir, "state.json"), "{not json");

        await Assert.ThrowsAsync<InvalidDataException>(() => Fx.NewStoreAsync(options));
    }

    [Fact]
    public async Task 恢复已保存的状态()
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
    public async Task 同步后落盘且可重新加载()
    {
        StoreOptions options = Fx.Options();
        using BookmarkStore store = await Fx.NewStoreAsync(options);

        await store.SyncAsync(Fx.StateOf(("b", Fx.ValidBookmark(RootFolders.Toolbar))), null, Fx.DeviceA);

        string raw = await File.ReadAllTextAsync(Path.Combine(options.DataDir, "state.json"));
        var reloaded = JsonSerializer.Deserialize<State>(raw, BmsyncJson.Storage)!;
        Assert.Single(reloaded.Items);

        // 临时文件不应残留
        Assert.False(File.Exists(Path.Combine(options.DataDir, "state.json.tmp")), "临时文件未被清理");
    }

    [Fact]
    public async Task 拒绝非法结果且不改动已落盘状态()
    {
        StoreOptions options = Fx.Options();
        using BookmarkStore store = await Fx.NewStoreAsync(options);
        await store.SyncAsync(Fx.StateOf(("b", Fx.ValidBookmark(RootFolders.Toolbar))), null, Fx.DeviceA);

        string path = Path.Combine(options.DataDir, "state.json");
        string before = await File.ReadAllTextAsync(path);

        // 一个 depth 超过上限的 state
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
    public async Task 同步前先建快照()
    {
        StoreOptions options = Fx.Options();
        using BookmarkStore store = await Fx.NewStoreAsync(options);

        // 第一次同步：状态为空，不产生快照
        await store.SyncAsync(Fx.StateOf(("b1", Fx.ValidBookmark(RootFolders.Toolbar))), null, Fx.DeviceA);

        // 第二次同步：应产生一个"本次同步前"的快照
        await store.SyncAsync(Fx.StateOf(
            ("b1", Fx.ValidBookmark(RootFolders.Toolbar)),
            ("b2", Fx.ValidBookmark(RootFolders.Unfiled))), null, Fx.DeviceA);

        IReadOnlyList<SnapshotInfo> snaps = await store.ListSnapshotsAsync();
        SnapshotInfo snap = Assert.Single(snaps);

        // 快照内容应是"同步前"的状态：只有 b1，没有 b2
        string raw = await File.ReadAllTextAsync(
            Path.Combine(options.DataDir, "history", snap.Id + ".json"));
        var content = JsonSerializer.Deserialize<State>(raw, BmsyncJson.Storage)!;

        Assert.Single(content.Items);
        Assert.Contains(Fx.KeyOf("b1"), content.Items.Keys);
    }

    [Fact]
    public async Task 清理超出保留份数的旧快照()
    {
        StoreOptions options = Fx.Options(); // HistoryKeep = 3
        using BookmarkStore store = await Fx.NewStoreAsync(options);
        State baseline = Fx.StateOf(("b1", Fx.ValidBookmark(RootFolders.Toolbar)));

        for (int i = 0; i < 8; i++)
        {
            await store.SyncAsync(baseline, null, Fx.DeviceA);
            await Task.Delay(2); // 快照名是毫秒级时间戳
        }

        int count = Directory.GetFiles(Path.Combine(options.DataDir, "history"), "*.json").Length;
        Assert.True(count <= options.HistoryKeep, $"快照数 {count} 超过保留上限 {options.HistoryKeep}");
    }

    /// <summary>
    /// 锁覆盖整个「读 → 合并 → 写」，所以并发同步不会丢更新。
    /// 100 次并发写入后，100 个书签必须一个不少。
    /// </summary>
    [Fact]
    public async Task 并发同步不丢更新()
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

        // 落盘文件也必须完整
        string raw = await File.ReadAllTextAsync(Path.Combine(options.DataDir, "state.json"));
        var reloaded = JsonSerializer.Deserialize<State>(raw, BmsyncJson.Storage)!;
        Assert.Equal(n, reloaded.Items.Count);
    }

    [Fact]
    public async Task 重复同步幂等()
    {
        using BookmarkStore store = await Fx.NewStoreAsync(Fx.Options());
        State incoming = Fx.StateOf(("b", Fx.ValidBookmark(RootFolders.Toolbar)));

        MergeResult first = await store.SyncAsync(incoming, null, Fx.DeviceA);
        for (int i = 0; i < 4; i++)
        {
            MergeResult again = await store.SyncAsync(incoming, null, Fx.DeviceA);
            Assert.True(Fx.SameStates(first.State, again.State), $"第 {i + 1} 次重复同步结果不同");
            Assert.Equal(0, again.Summary.Created);
        }
    }

    [Fact]
    public async Task 冲突环形缓冲()
    {
        using BookmarkStore store = await Fx.NewStoreAsync(Fx.Options());
        Item orig = Fx.ValidBookmark(RootFolders.Toolbar);
        string k = Fx.KeyOf("b");
        var baseMap = new Dictionary<string, string>(StringComparer.Ordinal) { [k] = orig.M };

        await store.SyncAsync(Fx.StateOf(("b", orig)), baseMap, Fx.DeviceA);

        // 制造多次真并发编辑。
        //
        // 关键：光有"客户端改了"不算冲突 —— base 记录的是双方上次见到的样子，
        // 只有当**两端都基于同一个基线改动了同一项**时才是并发编辑。
        // 所以下面每轮都先让服务端侧也改一次。
        for (int round = 0; round < 10; round++)
        {
            Item serverSide = Fx.ValidBookmark(RootFolders.Toolbar) with
            {
                N = "云端改" + round,
                M = Hlc.Encode(2000 + (round * 2), 0),
            };
            await store.SyncAsync(Fx.StateOf(("b", serverSide)), baseMap, Fx.DeviceA);

            Item clientSide = Fx.ValidBookmark(RootFolders.Toolbar) with
            {
                N = "本地改" + round,
                M = Hlc.Encode(2001 + (round * 2), 0),
            };

            int before = (await store.GetConflictsAsync(0)).Count;
            await store.SyncAsync(Fx.StateOf(("b", clientSide)), baseMap, Fx.DeviceB);

            int after = (await store.GetConflictsAsync(0)).Count;
            Assert.True(after > before, $"第 {round} 轮应记录冲突");
        }

        IReadOnlyList<Conflict> all = await store.GetConflictsAsync(0);
        Assert.True(all.Count <= 500, $"冲突缓冲 {all.Count} 条，超过上限 500");
        Assert.Equal(3, (await store.GetConflictsAsync(3)).Count);
    }

    [Fact]
    public async Task 冲突缓冲重启后恢复()
    {
        StoreOptions options = Fx.Options();
        using (BookmarkStore st = await Fx.NewStoreAsync(options))
        {
            // 走一次真实同步制造冲突，比直接调内部方法更接近线上路径
            Item orig = Fx.ValidBookmark(RootFolders.Toolbar);
            string k = Fx.KeyOf("b");
            var baseMap = new Dictionary<string, string>(StringComparer.Ordinal) { [k] = orig.M };
            await st.SyncAsync(Fx.StateOf(("b", orig)), baseMap, Fx.DeviceA);

            await st.SyncAsync(
                Fx.StateOf(("b", orig with { N = "云端", M = Hlc.Encode(300, 0) })), baseMap, Fx.DeviceA);
            await st.SyncAsync(
                Fx.StateOf(("b", orig with { N = "本地", M = Hlc.Encode(200, 0) })), baseMap, Fx.DeviceB);
        }

        using BookmarkStore reopened = await Fx.NewStoreAsync(options);
        Assert.NotEmpty(await reopened.GetConflictsAsync(0));
    }

    [Fact]
    public async Task GetState返回深拷贝()
    {
        using BookmarkStore store = await Fx.NewStoreAsync(Fx.Options());
        await store.SyncAsync(Fx.StateOf(("b", Fx.ValidBookmark(RootFolders.Toolbar))), null, Fx.DeviceA);

        State got = await store.GetStateAsync();
        got.Items.Remove(Fx.KeyOf("b"));

        Assert.Equal(1, await store.GetItemCountAsync());
    }

    [Fact]
    public async Task 空目录下快照列表为空()
    {
        using BookmarkStore store = await Fx.NewStoreAsync(Fx.Options());
        Assert.Empty(await store.ListSnapshotsAsync());
    }

    [Fact]
    public async Task ActiveCount只算活跃项()
    {
        using BookmarkStore store = await Fx.NewStoreAsync(Fx.Options());

        // 墓碑的 x 用**当前**时间：若用 Fx.T0（2023 年），它早于 90 天 TTL，
        // 会在同一次 Sync 里被 GC 掉，测到的 ItemCount 就变成 1 ——
        // 症状看起来像"合并丢了一项"，实际是测试数据过期了。
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        State incoming = Fx.StateOf(
            ("live", Fx.ValidBookmark(RootFolders.Toolbar)),
            ("dead", Fx.ValidBookmark(RootFolders.Unfiled) with { D = true, X = now, M = Hlc.Encode(200, 0) }));

        await store.SyncAsync(incoming, null, Fx.DeviceA);

        Assert.Equal(2, await store.GetItemCountAsync());
        Assert.Equal(1, await store.GetActiveCountAsync());
    }

    [Fact]
    public async Task 过期墓碑被GC清理()
    {
        // 上一条的镜像：确认 GC 确实在工作，而不只是"因为数据没过期所以没删"。
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
