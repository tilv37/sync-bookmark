namespace BookmarkSync.Domain.Tests;

/// <summary>
/// 合并算法的单元测试，逐条对应
/// 合并算法的测试用例（服务端从 Go 迁移到 .NET 10 时逐条移植）。
/// </summary>
/// <remarks>
/// 这个文件是整个项目里最重要的测试：合并出错的表现是<b>静默丢书签、
/// 用户完全无感</b>，所以除了逐条覆盖分支，最后还有一条随机化收敛测试。
/// </remarks>
public class MergeTests
{
    private static readonly string K = Fixtures.KeyOf("b");

    // ── 基础合并 ──────────────────────────────────────────────────────

    [Fact]
    public void 两侧都空()
    {
        MergeResult r = Merger.Merge(State.New(), State.New(), null, Fixtures.DeviceA, Fixtures.T0);

        Assert.Empty(r.State.Items);
        Assert.Equal("created=0 updated=0 deleted=0 unchanged=0", Fixtures.Summarize(r.Summary));
    }

    [Fact]
    public void 服务端为空客户端有数据()
    {
        // 工作电脑第一次同步：本地有书签，云端还是空的
        State incoming = Fixtures.StateOf(
            ("f1", Fixtures.Folder(RootFolders.Toolbar, "工作", 100, 0)),
            ("b1", Fixtures.Bookmark(Fixtures.KeyOf("f1"), "示例", "https://example.com", 100, 1)));

        MergeResult r = Merger.Merge(State.New(), incoming, null, Fixtures.DeviceB, Fixtures.T0);

        Assert.Equal(2, r.State.Items.Count);
        Assert.Equal(2, r.Summary.Created);
        Assert.Empty(r.Conflicts);
    }

    [Fact]
    public void 客户端为空服务端保留全部()
    {
        // 云端有数据，另一台设备本地什么都没有（例如换了新 profile）
        State server = Fixtures.StateOf(
            ("b1", Fixtures.Bookmark(RootFolders.Unfiled, "示例", "https://example.com", 100, 0)));

        MergeResult r = Merger.Merge(server, State.New(), null, Fixtures.DeviceB, Fixtures.T0);

        Assert.Single(r.State.Items);
        Assert.Equal(1, r.Summary.Unchanged);
        Assert.Equal(0, r.Summary.Created);
    }

    [Fact]
    public void 两侧互不相交()
    {
        State server = Fixtures.StateOf(
            ("a", Fixtures.Bookmark(RootFolders.Toolbar, "A", "https://a.example", 100, 0)));
        State incoming = Fixtures.StateOf(
            ("b", Fixtures.Bookmark(RootFolders.Toolbar, "B", "https://b.example", 100, 0)));

        MergeResult r = Merger.Merge(server, incoming, null, Fixtures.DeviceB, Fixtures.T0);

        Assert.Equal(2, r.State.Items.Count);
        Assert.Equal(1, r.Summary.Created);
        Assert.Equal(1, r.Summary.Unchanged);
    }

    [Fact]
    public void LWW_较新者胜()
    {
        Item older = Fixtures.Bookmark(RootFolders.Toolbar, "旧标题", "https://example.com", 100, 0);
        Item newer = Fixtures.Bookmark(RootFolders.Toolbar, "新标题", "https://example.com", 200, 0);

        State s = State.New();
        s.Items[K] = older;
        State i = State.New();
        i.Items[K] = newer;

        MergeResult r = Merger.Merge(s, i, null, Fixtures.DeviceB, Fixtures.T0);
        Assert.Equal("新标题", r.State.Items[K].N);
        Assert.Equal(1, r.Summary.Updated);
    }

    [Fact]
    public void LWW_服务端较新也胜()
    {
        Item older = Fixtures.Bookmark(RootFolders.Toolbar, "旧标题", "https://example.com", 100, 0);
        Item newer = Fixtures.Bookmark(RootFolders.Toolbar, "新标题", "https://example.com", 200, 0);

        State s = State.New();
        s.Items[K] = newer;
        State i = State.New();
        i.Items[K] = older;

        MergeResult r = Merger.Merge(s, i, null, Fixtures.DeviceB, Fixtures.T0);
        Assert.Equal("新标题", r.State.Items[K].N);
    }

    [Fact]
    public void 时间戳相同且内容相同原样保留()
    {
        Item it = Fixtures.Bookmark(RootFolders.Toolbar, "标题", "https://example.com", 100, 3);
        State s = State.New();
        s.Items[K] = it;
        State i = State.New();
        i.Items[K] = it;

        MergeResult r = Merger.Merge(s, i, null, Fixtures.DeviceB, Fixtures.T0);

        Assert.Equal(it, r.State.Items[K]);
        Assert.Empty(r.Conflicts);
        Assert.Equal(1, r.Summary.Unchanged);
    }

    [Fact]
    public void 时间戳相同内容不同必须确定()
    {
        State s = State.New();
        State i = State.New();
        s.Items[K] = Fixtures.Bookmark(RootFolders.Toolbar, "AAA", "https://example.com", 100, 3);
        i.Items[K] = Fixtures.Bookmark(RootFolders.Toolbar, "BBB", "https://example.com", 100, 3);

        string? first = null;
        for (int run = 0; run < 50; run++)
        {
            MergeResult r = Merger.Merge(s, i, null, Fixtures.DeviceB, Fixtures.T0);
            first ??= r.State.Items[K].N;
            Assert.Equal(first, r.State.Items[K].N);
        }

        MergeResult r0 = Merger.Merge(s, i, null, Fixtures.DeviceB, Fixtures.T0);
        Assert.Single(r0.Conflicts);
    }

    // ── 删除与墓碑 ────────────────────────────────────────────────────

    [Fact]
    public void 删除会传播()
    {
        Item alive = Fixtures.Bookmark(RootFolders.Toolbar, "标题", "https://example.com", 100, 0);
        Item dead = Fixtures.Tomb(alive, Fixtures.T0, 200, 0);

        State s = State.New();
        State i = State.New();
        s.Items[K] = alive;
        i.Items[K] = dead;

        MergeResult r = Merger.Merge(s, i, null, Fixtures.DeviceB, Fixtures.T0);

        Assert.True(r.State.Items[K].D, "客户端的删除（较新）没有传播到服务端状态");
        Assert.Equal(1, r.Summary.Deleted);
    }

    [Fact]
    public void 较旧的删除输给较新的修改()
    {
        // 设备 A 删了书签，设备 B 在此之后又改了这个书签（说明它还在用）
        Item dead = Fixtures.Tomb(
            Fixtures.Bookmark(RootFolders.Toolbar, "标题", "https://example.com", 100, 0), Fixtures.T0, 100, 0);
        Item revived = Fixtures.Bookmark(RootFolders.Toolbar, "标题", "https://example.com", 200, 0);

        State s = State.New();
        State i = State.New();
        s.Items[K] = dead;
        i.Items[K] = revived;

        MergeResult r = Merger.Merge(s, i, null, Fixtures.DeviceB, Fixtures.T0);
        Assert.False(r.State.Items[K].D, "较旧的删除不应覆盖较新的修改");
    }

    [Fact]
    public void 两侧都是墓碑()
    {
        Item alive = Fixtures.Bookmark(RootFolders.Toolbar, "标题", "https://example.com", 100, 0);
        State s = State.New();
        State i = State.New();
        s.Items[K] = Fixtures.Tomb(alive, Fixtures.T0, 200, 0);
        i.Items[K] = Fixtures.Tomb(alive, Fixtures.T0, 100, 0); // 较旧的删除

        MergeResult r = Merger.Merge(s, i, null, Fixtures.DeviceB, Fixtures.T0);
        Item got = r.State.Items[K];

        Assert.True(got.D, "两侧都是墓碑，结果必须是墓碑");
        Assert.Equal(Hlc.Encode(200, 0), got.M);
        // x 保留较早的那个：GC 按 x 判定，越早清理越省空间
        Assert.Equal(Fixtures.T0, got.X);
    }

    [Fact]
    public void 较新的重新添加让书签复活()
    {
        // 墓碑 + 客户端以更新的时间戳重新添加同一 URL → 书签复活
        Item alive = Fixtures.Bookmark(RootFolders.Toolbar, "标题", "https://example.com", 100, 0);

        State s = State.New();
        State i = State.New();
        s.Items[K] = Fixtures.Tomb(alive, Fixtures.T0, 200, 0);
        i.Items[K] = alive with { M = Hlc.Encode(300, 0), D = false, X = 0 };

        MergeResult r = Merger.Merge(s, i, null, Fixtures.DeviceB, Fixtures.T0);

        Assert.False(r.State.Items[K].D, "较新的重新添加应让书签复活");
        Assert.Equal("标题", r.State.Items[K].N);
    }

    // ── 幂等性与收敛性 ────────────────────────────────────────────────

    /// <summary>
    /// 重复同步同一个请求，结果必须完全一致。
    /// 这是"用户连点三次同步按钮"不会把书签搞乱的前提。
    /// </summary>
    [Fact]
    public void 幂等()
    {
        State server = Fixtures.StateOf(
            ("f", Fixtures.Folder(RootFolders.Toolbar, "目录", 100, 0)),
            ("b1", Fixtures.Bookmark(Fixtures.KeyOf("f"), "一", "https://one.example", 100, 1)),
            ("b2", Fixtures.Tomb(
                Fixtures.Bookmark(RootFolders.Toolbar, "二", "https://two.example", 100, 0), Fixtures.T0, 100, 2)));
        State incoming = Fixtures.StateOf(
            ("f", Fixtures.Folder(RootFolders.Toolbar, "目录", 100, 0)),
            ("b1", Fixtures.Bookmark(Fixtures.KeyOf("f"), "一改", "https://one.example", 200, 0)),
            ("b3", Fixtures.Bookmark(RootFolders.Menu, "三", "https://three.example", 200, 1)),
            ("b2", Fixtures.Tomb(
                Fixtures.Bookmark(RootFolders.Toolbar, "二", "https://two.example", 100, 0), Fixtures.T0, 100, 2)));

        State once = Merger.Merge(server, incoming, null, Fixtures.DeviceB, Fixtures.T0).State;
        for (int n = 0; n < 5; n++)
        {
            State again = Merger.Merge(server, incoming, null, Fixtures.DeviceB, Fixtures.T0).State;
            Assert.True(Fixtures.SameStates(once, again), $"第 {n + 1} 次重复同步结果不同");
        }

        // 把结果再当服务端、拿同一个请求合并一次，也不应变化
        State twice = Merger.Merge(once, incoming, null, Fixtures.DeviceB, Fixtures.T0).State;
        Assert.True(Fixtures.SameStates(once, twice), "以合并结果为服务端再次合并，结果发生了变化");
    }

    /// <summary>乱序同步最终必须收敛：无论 A、B 的到达顺序如何，服务端的权威状态相同。</summary>
    [Fact]
    public void 乱序也收敛()
    {
        State b = Fixtures.StateOf(
            ("f", Fixtures.Folder(RootFolders.Toolbar, "目录", 100, 0)),
            ("b1", Fixtures.Bookmark(Fixtures.KeyOf("f"), "一", "https://one.example", 100, 1)),
            ("b2", Fixtures.Bookmark(RootFolders.Unfiled, "二", "https://two.example", 100, 2)));
        State patchA = Fixtures.StateOf(
            ("b1", Fixtures.Bookmark(Fixtures.KeyOf("f"), "一改", "https://one.example", 300, 0)),
            ("b3", Fixtures.Bookmark(RootFolders.Unfiled, "三", "https://three.example", 250, 0)));
        State patchB = Fixtures.StateOf(
            ("b2", Fixtures.Bookmark(RootFolders.Unfiled, "二改", "https://two.example", 400, 0)));

        State ab = Merger.Merge(
            Merger.Merge(b, patchA, null, Fixtures.DeviceA, Fixtures.T0).State,
            patchB, null, Fixtures.DeviceB, Fixtures.T0).State;
        State ba = Merger.Merge(
            Merger.Merge(b, patchB, null, Fixtures.DeviceB, Fixtures.T0).State,
            patchA, null, Fixtures.DeviceA, Fixtures.T0).State;

        Assert.True(
            Fixtures.SameStates(ab, ba),
            $"A→B 与 B→A 顺序结果不一致：\n AB: {Describe(ab)}\n BA: {Describe(ba)}");

        Assert.Equal(4, ab.Items.Count);
        Assert.Equal("二改", ab.Items[Fixtures.KeyOf("b2")].N);
        Assert.Equal("三", ab.Items[Fixtures.KeyOf("b3")].N);
    }

    private static string Describe(State s) =>
        string.Join(", ", s.KeysSorted().Select(k => $"{k[..4]}={s.Items[k].N}({s.Items[k].M})"));

    // ── 冲突检测 ──────────────────────────────────────────────────────

    /// <summary>
    /// 首次同步（base 为空）绝不能报冲突，否则用户第一次点同步就被
    /// 一屏冲突记录淹没，这个功能就废了。
    /// </summary>
    [Fact]
    public void base为空时不报冲突()
    {
        State server = Fixtures.StateOf(
            ("b", Fixtures.Bookmark(RootFolders.Toolbar, "云端标题", "https://example.com", 200, 0)));
        State incoming = Fixtures.StateOf(
            ("b", Fixtures.Bookmark(RootFolders.Toolbar, "本地标题", "https://example.com", 100, 0)));

        MergeResult r = Merger.Merge(server, incoming, null, Fixtures.DeviceB, Fixtures.T0);

        Assert.Empty(r.Conflicts);
        Assert.Equal("云端标题", r.State.Items[K].N);
    }

    [Fact]
    public void 单边编辑不是冲突()
    {
        Item orig = Fixtures.Bookmark(RootFolders.Toolbar, "原标题", "https://example.com", 100, 0);
        var baseMap = new Dictionary<string, string>(StringComparer.Ordinal) { [K] = orig.M };

        // 只有客户端改
        MergeResult r1 = Merger.Merge(
            Fixtures.StateOf(("b", orig)),
            Fixtures.StateOf(("b", Fixtures.Bookmark(RootFolders.Toolbar, "新标题", "https://example.com", 300, 0))),
            baseMap, Fixtures.DeviceB, Fixtures.T0);
        Assert.Empty(r1.Conflicts);

        // 只有服务端改
        MergeResult r2 = Merger.Merge(
            Fixtures.StateOf(("b", Fixtures.Bookmark(RootFolders.Toolbar, "云端改", "https://example.com", 300, 0))),
            Fixtures.StateOf(("b", orig)),
            baseMap, Fixtures.DeviceB, Fixtures.T0);
        Assert.Empty(r2.Conflicts);
    }

    [Fact]
    public void 检出真正的并发编辑()
    {
        Item orig = Fixtures.Bookmark(RootFolders.Toolbar, "原标题", "https://example.com", 100, 0);
        var baseMap = new Dictionary<string, string>(StringComparer.Ordinal) { [K] = orig.M };

        State server = Fixtures.StateOf(
            ("b", Fixtures.Bookmark(RootFolders.Toolbar, "云端改的", "https://example.com", 300, 0)));
        State incoming = Fixtures.StateOf(
            ("b", Fixtures.Bookmark(RootFolders.Toolbar, "本地改的", "https://example.com", 200, 0)));

        MergeResult r = Merger.Merge(server, incoming, baseMap, Fixtures.DeviceB, Fixtures.T0);

        Conflict c = Assert.Single(r.Conflicts);
        Assert.Equal(ConflictReasons.ConcurrentEdit, c.Reason);
        Assert.Equal("title", c.Field);
        Assert.Equal(ConflictWinners.Server, c.Winner);
        Assert.Equal(Fixtures.DeviceB, c.Loser);
        Assert.Equal("云端改的", c.WinnerValue);
        Assert.Equal("本地改的", c.LoserValue);
        Assert.Equal("https://example.com", c.Url);

        // 冲突是纯观测：决胜结果仍按 LWW
        Assert.Equal("云端改的", r.State.Items[K].N);
    }

    public static TheoryData<string, Item, Item, string> ConflictFieldCases()
    {
        Item orig = Fixtures.Bookmark(RootFolders.Toolbar, "标题", "https://example.com", 100, 0);

        var data = new TheoryData<string, Item, Item, string>
        {
            { "标题变了", orig, Fixtures.Bookmark(RootFolders.Toolbar, "新", "https://example.com", 200, 0), "title" },
            { "URL 变了", orig, Fixtures.Bookmark(RootFolders.Toolbar, "标题", "https://other.example", 200, 0), "url" },
            { "父目录变了", orig, Fixtures.Bookmark(RootFolders.Menu, "标题", "https://example.com", 200, 0), "parent" },
            // 书签变成文件夹：P 相同、N 相同，只有 T 不同 —— FirstDifferingField
            // 按 parent→type→title→url 的固定顺序取第一个差异，所以是 "type"
            { "书签变成文件夹", orig, Fixtures.Folder(RootFolders.Toolbar, "标题", 200, 0), "type" },
            // P 与 T 同时不同：固定顺序保证一定报 "parent"，不随同步轮次变化
            { "父目录和类型都变了", orig, Fixtures.Folder(RootFolders.Menu, "标题", 200, 0), "parent" },
        };

        return data;
    }

    [Theory]
    [MemberData(nameof(ConflictFieldCases))]
    public void 冲突记录正确的字段(string name, Item serverItem, Item clientItem, string wantField)
    {
        Item orig = Fixtures.Bookmark(RootFolders.Toolbar, "标题", "https://example.com", 100, 0);
        var baseMap = new Dictionary<string, string>(StringComparer.Ordinal) { [K] = orig.M };

        State s = State.New();
        State i = State.New();
        // 两侧都"改过"：服务端基线也要晚于 base
        s.Items[K] = serverItem with { M = Hlc.Encode(300, 0) };
        i.Items[K] = clientItem;

        MergeResult r = Merger.Merge(s, i, baseMap, Fixtures.DeviceB, Fixtures.T0);

        Conflict c = Assert.Single(r.Conflicts);
        Assert.Equal(wantField, c.Field);
        _ = name;
    }

    // ── 不变量 ────────────────────────────────────────────────────────

    [Fact]
    public void 不修改入参()
    {
        State server = Fixtures.StateOf(
            ("b", Fixtures.Bookmark(RootFolders.Toolbar, "一", "https://one.example", 100, 0)));
        State incoming = Fixtures.StateOf(
            ("b", Fixtures.Bookmark(RootFolders.Toolbar, "二", "https://one.example", 200, 0)));
        var baseMap = new Dictionary<string, string>(StringComparer.Ordinal) { [K] = Hlc.Encode(50, 0) };

        State serverBefore = server.Clone();
        State incomingBefore = incoming.Clone();
        var baseBefore = new Dictionary<string, string>(baseMap, StringComparer.Ordinal);

        _ = Merger.Merge(server, incoming, baseMap, Fixtures.DeviceB, Fixtures.T0);

        Assert.True(Fixtures.SameStates(server, serverBefore), "Merge 修改了入参 server");
        Assert.True(Fixtures.SameStates(incoming, incomingBefore), "Merge 修改了入参 incoming");
        Assert.Equal(baseBefore.Count, baseMap.Count);
        foreach ((string k, string v) in baseMap)
        {
            Assert.Equal(baseBefore[k], v);
        }
    }

    /// <summary>
    /// 服务端有的 item 绝不会凭空消失 —— 删除必须以墓碑的形式显式表达。
    /// 这是"合并绝不会静默丢书签"这条承诺的直接体现。
    /// </summary>
    [Fact]
    public void 绝不丢服务端item()
    {
        State server = Fixtures.StateOf(
            ("a", Fixtures.Bookmark(RootFolders.Toolbar, "一", "https://one.example", 100, 0)),
            ("b", Fixtures.Bookmark(RootFolders.Toolbar, "二", "https://two.example", 100, 0)),
            ("c", Fixtures.Folder(RootFolders.Unfiled, "目录", 100, 0)));

        MergeResult r = Merger.Merge(server, State.New(), null, Fixtures.DeviceB, Fixtures.T0);

        foreach (string k in server.KeysSorted())
        {
            Assert.Contains(k, r.State.Items.Keys);
        }
    }

    [Fact]
    public void 合并结果自洽()
    {
        State server = Fixtures.StateOf(
            ("f", Fixtures.Folder(RootFolders.Toolbar, "目录", 100, 0)),
            ("b", Fixtures.Tomb(
                Fixtures.Bookmark(Fixtures.KeyOf("f"), "一", "https://one.example", 100, 0), Fixtures.T0, 100, 0)));
        State incoming = Fixtures.StateOf(
            ("f", Fixtures.Folder(RootFolders.Toolbar, "目录", 100, 0)),
            ("b", Fixtures.Bookmark(Fixtures.KeyOf("f"), "一", "https://one.example", 300, 0))); // 复活

        MergeResult r = Merger.Merge(server, incoming, null, Fixtures.DeviceB, Fixtures.T0);

        ValidationResult v = r.State.Validate();
        Assert.True(v.IsValid, $"合并结果未通过自身的 Validate: {v.Error}");
    }

    // ── 随机化性质测试 ────────────────────────────────────────────────
    //
    // 手写用例只能覆盖"想得到的场景"。真正能抓住边界 bug 的是随机化收敛测试：
    // 生成随机的操作序列，以任意顺序重放，服务端必须收敛到同一个状态。

    [Fact]
    public void 随机化收敛()
    {
        var rnd = new Random(20260928);
        const int devices = 3;

        var truth = new State[devices];
        for (int d = 0; d < devices; d++)
        {
            truth[d] = State.New();
        }

        long clock = 1000;

        for (int op = 0; op < 400; op++)
        {
            clock += rnd.Next(3);
            int d = rnd.Next(devices);
            int action = rnd.Next(4);
            string k = Fixtures.KeyOf($"item{(char)('a' + rnd.Next(20))}");

            switch (action)
            {
                case 0: // 新增或覆盖
                    truth[d].Items[k] = Fixtures.Bookmark(
                        RootFolders.Toolbar, "t" + k[..4], "https://e.example/" + k[..4], clock, rnd.Next(50));
                    break;

                case 1: // 删除（墓碑）
                    if (truth[d].Items.TryGetValue(k, out Item prev))
                    {
                        truth[d].Items[k] = Fixtures.Tomb(prev, Fixtures.T0, clock, rnd.Next(50));
                    }

                    break;

                case 2: // 改标题
                    if (truth[d].Items.TryGetValue(k, out Item prev2))
                    {
                        truth[d].Items[k] = prev2 with
                        {
                            N = "changed" + k[..4],
                            M = Hlc.Encode(clock, rnd.Next(50)),
                        };
                    }

                    break;

                case 3:
                    // 删除本地副本（模拟设备换了新 profile，上报空状态）：
                    // 此时设备会上报它缓存里的内容，这里不改动 truth
                    break;
            }
        }

        // 把三台设备的副本以各种顺序合并到服务端
        State server = State.New();
        int[] order = [0, 1, 2];
        Shuffle(rnd, order);
        foreach (int d in order)
        {
            server = Merger.Merge(server, truth[d], null, "dev", Fixtures.T0).State;
        }

        // 再以另一个顺序合并一遍，结果必须不变（收敛）
        State again = State.New();
        foreach (int d in new[] { 2, 0, 1 })
        {
            again = Merger.Merge(again, truth[d], null, "dev", Fixtures.T0).State;
        }

        Assert.True(
            Fixtures.SameStates(server, again),
            $"不同合并顺序得到不同结果，收敛性被破坏：\n A: {Describe(server)}\n B: {Describe(again)}");

        ValidationResult v = server.Validate();
        Assert.True(v.IsValid, $"随机化合并结果未通过 Validate: {v.Error}");

        // 关键：服务端的结果必须包含每台设备上报过的**所有** key
        var allKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (State t in truth)
        {
            foreach (string k in t.Items.Keys)
            {
                allKeys.Add(k);
            }
        }

        foreach (string k in allKeys)
        {
            Assert.Contains(k, server.Items.Keys);
        }
    }

    private static void Shuffle(Random rnd, int[] values)
    {
        for (int i = values.Length - 1; i > 0; i--)
        {
            int j = rnd.Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }
}
