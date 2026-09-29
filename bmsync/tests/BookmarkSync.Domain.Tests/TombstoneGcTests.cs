namespace BookmarkSync.Domain.Tests;

/// <summary>
/// 墓碑 GC 的单元测试，逐条对应
/// 墓碑 GC 的测试用例（同上）。
/// </summary>
/// <remarks>
/// GC 出 bug 的后果是静默丢书签，所以这个文件的每条用例都在钉一条安全约束。
/// </remarks>
public class TombstoneGcTests
{
    private static readonly long Day = 24 * 60 * 60 * 1000L;
    private static readonly TimeSpan Ttl90 = TimeSpan.FromDays(90);

    [Fact]
    public void 只清理过期墓碑()
    {
        long now = Fixtures.T0;

        var items = State.NewItemsDictionary();
        // 活跃项：无论多旧都不动
        items[Fixtures.KeyOf("live-old")] = Fixtures.Bookmark(RootFolders.Toolbar, "很久以前加的", "https://a.example", 1, 0);
        items[Fixtures.KeyOf("live-new")] = Fixtures.Bookmark(RootFolders.Toolbar, "刚加的", "https://b.example", 999_999, 0);
        // 墓碑 91 天前 → 应被清理
        items[Fixtures.KeyOf("dead-91d")] =
            Fixtures.Tomb(Fixtures.Bookmark(RootFolders.Toolbar, "x", "https://c.example", 100, 0), now - 91 * Day, 100, 0);
        // 墓碑 89 天前 → 保留
        items[Fixtures.KeyOf("dead-89d")] =
            Fixtures.Tomb(Fixtures.Bookmark(RootFolders.Toolbar, "y", "https://d.example", 100, 0), now - 89 * Day, 100, 0);
        // 墓碑刚删 → 保留
        items[Fixtures.KeyOf("dead-today")] =
            Fixtures.Tomb(Fixtures.Bookmark(RootFolders.Toolbar, "z", "https://e.example", 100, 0), now - 1 * Day, 100, 0);

        (Dictionary<string, Item> output, int removed) = TombstoneGc.Collect(items, now, Ttl90);

        Assert.Equal(1, removed);
        Assert.DoesNotContain(Fixtures.KeyOf("dead-91d"), output.Keys);
        foreach (string label in new[] { "dead-89d", "dead-today", "live-old", "live-new" })
        {
            Assert.Contains(Fixtures.KeyOf(label), output.Keys);
        }

        Assert.Equal(4, output.Count);
    }

    [Fact]
    public void 绝不碰活跃项()
    {
        var items = State.NewItemsDictionary();
        items[Fixtures.KeyOf("a")] = Fixtures.Bookmark(RootFolders.Toolbar, "a", "https://a.example", 1, 0);
        items[Fixtures.KeyOf("b")] = Fixtures.Folder(RootFolders.Unfiled, "b", 1, 0);
        items[Fixtures.KeyOf("c")] = Fixtures.Folder(RootFolders.Menu, "c", 1, 0);

        // TTL = 0，什么都该过期
        (Dictionary<string, Item> output, int removed) = TombstoneGc.Collect(items, Fixtures.T0, TimeSpan.Zero);

        Assert.Equal(0, removed);
        Assert.Equal(3, output.Count);
    }

    /// <summary>
    /// x == 0 表示"不知道什么时候删的"。宁可留着一个墓碑（浪费几百字节），
    /// 也不能删掉一个"删除时间未知"的事实 —— 那会让删除同步静默失效。
    /// </summary>
    [Fact]
    public void 保留没有删除时间的墓碑()
    {
        Item it = Fixtures.Bookmark(RootFolders.Toolbar, "x", "https://x.example", 100, 0) with { D = true, X = 0 };

        var items = State.NewItemsDictionary();
        items[Fixtures.KeyOf("x")] = it;

        (Dictionary<string, Item> output, int removed) =
            TombstoneGc.Collect(items, Fixtures.T0, TimeSpan.Zero);

        Assert.Equal(0, removed);
        Assert.Contains(Fixtures.KeyOf("x"), output.Keys);
    }

    [Fact]
    public void 边界_空字典()
    {
        (Dictionary<string, Item> output, int removed) =
            TombstoneGc.Collect(State.NewItemsDictionary(), Fixtures.T0, Ttl90);

        Assert.Equal(0, removed);
        Assert.Empty(output);
    }

    [Fact]
    public void 边界_恰好等于TTL()
    {
        long at = Fixtures.T0 - (long)Ttl90.TotalMilliseconds;
        Item it = Fixtures.Tomb(Fixtures.Bookmark(RootFolders.Toolbar, "x", "https://x.example", 1, 0), at, 1, 0);

        // 判定是 x < cutoff（严格小于），恰好等于边界时保留
        var items = State.NewItemsDictionary();
        items[Fixtures.KeyOf("x")] = it;
        Assert.Equal(0, TombstoneGc.Collect(items, Fixtures.T0, Ttl90).Removed);

        // 再早 1 毫秒就该被清理
        items[Fixtures.KeyOf("x")] = it with { X = at - 1 };
        Assert.Equal(1, TombstoneGc.Collect(items, Fixtures.T0, Ttl90).Removed);
    }

    [Fact]
    public void 传入的字典不被修改()
    {
        var items = State.NewItemsDictionary();
        items[Fixtures.KeyOf("d")] =
            Fixtures.Tomb(Fixtures.Bookmark(RootFolders.Toolbar, "x", "https://x.example", 1, 0), Fixtures.T0 - Day, 1, 0);

        TombstoneGc.Collect(items, Fixtures.T0, Ttl90);

        Assert.Single(items);
    }

    [Fact]
    public void CollectFrom_清理并返回新State()
    {
        State s = Fixtures.StateOf(
            ("live", Fixtures.Bookmark(RootFolders.Toolbar, "活", "https://a.example", 1, 0)),
            ("dead", Fixtures.Tomb(Fixtures.Bookmark(RootFolders.Toolbar, "死", "https://b.example", 1, 0), Fixtures.T0 - 100 * Day, 1, 0)));

        (State after, int removed) = TombstoneGc.CollectFrom(s, Fixtures.T0, Ttl90);

        Assert.Equal(1, removed);
        Assert.Single(after.Items);

        // 无事可做时返回**同一个** State 对象，避免无谓的分配。
        // 这一点是移植时特意保留的：Go 版靠"不替换 map 指针"达成同样效果，
        // 这里靠"不返回新对象"达成，且返回值能直接看出来。
        (State again, int removed2) = TombstoneGc.CollectFrom(after, Fixtures.T0, Ttl90);
        Assert.Equal(0, removed2);
        Assert.Same(after, again);
    }

    /// <summary>
    /// 完整生命周期：创建 → 同步 → 删除 → 同步 → GC 清理。
    /// 确认墓碑真的会在 90 天后消失，不会无限堆积。
    /// </summary>
    [Fact]
    public void 墓碑完整生命周期()
    {
        string k = Fixtures.KeyOf("b");
        State s = State.New();
        s.Items[k] = Fixtures.Bookmark(RootFolders.Toolbar, "标题", "https://example.com", 100, 0);

        // 100 天后被删除
        long deleteAt = Fixtures.T0 + 100 * Day;
        s.Items[k] = Fixtures.Tomb(s.Items[k], deleteAt, 200, 0);
        Assert.Equal(0, s.CountActive());

        // 删除后 89 天：仍在
        (State s1, int f1) = TombstoneGc.CollectFrom(s, deleteAt + 89 * Day, Ttl90);
        Assert.Equal(0, f1);
        Assert.Single(s1.Items);

        // 删除后 91 天：清理
        (State s2, int f2) = TombstoneGc.CollectFrom(s1, deleteAt + 91 * Day, Ttl90);
        Assert.Equal(1, f2);
        Assert.Empty(s2.Items);
    }
}
