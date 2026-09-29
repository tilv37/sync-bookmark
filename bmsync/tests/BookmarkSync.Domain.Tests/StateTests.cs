namespace BookmarkSync.Domain.Tests;

/// <summary>
/// <see cref="State"/> 的单元测试，逐条对应
/// 数据模型与校验的测试用例（同上）。
/// </summary>
public class StateTests
{
    [Fact]
    public void Validate_接受结构合法的state()
    {
        State s = Fixtures.StateOf(
            ("f", Fixtures.ValidFolder(RootFolders.Toolbar)),
            ("b", Fixtures.ValidBookmark(Fixtures.KeyOf("f"))),
            ("top", Fixtures.ValidBookmark(RootFolders.Unfiled)));

        Assert.True(s.Validate().IsValid);
    }

    /// <summary>硬错误：会导致解析出错或资源失控的输入必须被拒绝。</summary>
    public static TheoryData<string, Action<State>, string> HardErrorCases()
    {
        var data = new TheoryData<string, Action<State>, string>();

        data.Add("key 太短", s => s.Items["ab"] = Fixtures.ValidBookmark(RootFolders.Toolbar), "key 非法");
        data.Add(
            "key 非十六进制",
            s =>
            {
                s.Items.Remove(Fixtures.KeyOf("b"));
                s.Items[new string('z', 32)] = Fixtures.ValidBookmark(RootFolders.Toolbar);
            },
            "key 非法");
        data.Add(
            "类型非法",
            s => s.Items[Fixtures.KeyOf("b")] = Fixtures.ValidBookmark(RootFolders.Toolbar) with { T = "x" },
            "类型非法");
        data.Add(
            "书签缺 url",
            s => s.Items[Fixtures.KeyOf("b")] = Fixtures.ValidBookmark(RootFolders.Toolbar) with { U = string.Empty },
            "必须有 url");
        data.Add(
            "文件夹带 url",
            s => s.Items[Fixtures.KeyOf("b")] = Fixtures.ValidFolder(RootFolders.Toolbar) with { U = "https://x.example" },
            "不应有 url");
        data.Add(
            "m 非法",
            s => s.Items[Fixtures.KeyOf("b")] = Fixtures.ValidBookmark(RootFolders.Toolbar) with { M = "yesterday" },
            "不是合法 HLC");
        data.Add(
            "a 非法",
            s => s.Items[Fixtures.KeyOf("b")] = Fixtures.ValidBookmark(RootFolders.Toolbar) with { A = "???" },
            "不是合法 HLC");
        data.Add(
            "标题超长",
            s => s.Items[Fixtures.KeyOf("b")] =
                Fixtures.ValidBookmark(RootFolders.Toolbar) with { N = new string('x', Limits.MaxTitleLen + 1) },
            "标题长度");
        data.Add(
            "url 超长",
            s => s.Items[Fixtures.KeyOf("b")] = Fixtures.ValidBookmark(RootFolders.Toolbar) with
            {
                U = "https://x.example/" + new string('y', Limits.MaxUrlLen),
            },
            "url 长度");
        data.Add(
            "深度超限",
            s =>
            {
                var fresh = State.New();
                string parent = RootFolders.Toolbar;
                for (int i = 0; i <= Limits.MaxDepth + 2; i++)
                {
                    string fk = Fixtures.KeyOf($"deep{(char)('a' + (i % 26))}{(char)('a' + (i / 26))}");
                    fresh.Items[fk] = Fixtures.ValidFolder(parent);
                    parent = fk;
                }

                foreach ((string k, Item v) in fresh.Items)
                {
                    s.Items[k] = v;
                }
            },
            "深度");

        return data;
    }

    [Theory]
    [MemberData(nameof(HardErrorCases))]
    public void Validate_硬错误必须拒绝(string name, Action<State> mutate, string wantSub)
    {
        State s = Fixtures.StateOf(("b", Fixtures.ValidBookmark(RootFolders.Toolbar)));
        mutate(s);

        ValidationResult res = s.Validate();
        Assert.False(res.IsValid, $"应当拒绝却通过了：{name}");
        Assert.Contains(wantSub, res.Error!, StringComparison.Ordinal);
    }

    /// <summary>版本不符单独测：<c>State.V</c> 是 init 属性，没法用 <see cref="Action{T}"/> 改。</summary>
    [Fact]
    public void Validate_版本不符必须拒绝()
    {
        State withBadVersion = new() { V = 99 };
        ValidationResult r = withBadVersion.Validate();

        Assert.False(r.IsValid);
        Assert.Contains("schema 版本", r.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_数量超限必须拒绝()
    {
        // 只造 key 集合，item 全用同��个合法值 —— 目的是把 item 数顶到上限之上，
        // 而不关心每个 item 自己的内容。
        State s = State.New();
        for (int i = 0; i <= Limits.MaxItems; i++)
        {
            s.Items[Fixtures.KeyOf($"bulk{i}")] = Fixtures.ValidBookmark(RootFolders.Toolbar);
        }

        ValidationResult r = s.Validate();
        Assert.False(r.IsValid);
        Assert.Contains("超过上限", r.Error!, StringComparison.Ordinal);
    }

    /// <summary>软警告：树结构问题只记录不拒绝。</summary>
    /// <remarks>
    /// 让整份上传因为一个孤立节点被拒，对用户来说比跳过那个节点糟糕得多。
    /// </remarks>
    public static TheoryData<string, Action<State>, string> SoftWarningCases()
    {
        var data = new TheoryData<string, Action<State>, string>();

        data.Add(
            "父节点缺失",
            s => s.Items[Fixtures.KeyOf("b")] =
                s.Items[Fixtures.KeyOf("b")] with { P = Fixtures.KeyOf("ghost") },
            "父节点");

        data.Add(
            "父节点不是文件夹",
            s => s.Items[Fixtures.KeyOf("b")] =
                s.Items[Fixtures.KeyOf("b")] with { P = Fixtures.KeyOf("other") },
            "不是文件夹");

        data.Add(
            "存活项的父节点已删除",
            s =>
            {
                s.Items[Fixtures.KeyOf("f")] = Fixtures.Tomb(Fixtures.ValidFolder(RootFolders.Toolbar), Fixtures.T0, 200, 0);
                s.Items[Fixtures.KeyOf("b")] = s.Items[Fixtures.KeyOf("b")] with { P = Fixtures.KeyOf("f") };
            },
            "已删除");

        data.Add(
            "父链成环",
            s =>
            {
                string a = Fixtures.KeyOf("cyc-a"), b = Fixtures.KeyOf("cyc-b");
                s.Items[a] = Fixtures.ValidFolder(b);
                s.Items[b] = Fixtures.ValidFolder(a);
            },
            "异常");

        return data;
    }

    [Theory]
    [MemberData(nameof(SoftWarningCases))]
    public void Validate_结构问题记为警告(string name, Action<State> mutate, string wantWarn)
    {
        State s = Fixtures.StateOf(
            ("b", Fixtures.ValidBookmark(RootFolders.Toolbar)),
            ("other", Fixtures.ValidBookmark(RootFolders.Menu)));

        mutate(s);

        ValidationResult r = s.Validate();

        // 名字带上，断言失败时能立刻看出是哪个场景退化了 ——
        // 这组用例全是"结构畸形"，只看警告文本很容易分不清是谁的问题。
        Assert.True(r.IsValid, $"[{name}] 结构问题应记为警告而非硬错误，却返回了：{r.Error}");

        string joined = string.Join("; ", r.Warnings);
        Assert.Contains(wantWarn, joined, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_墓碑缺删除时间只警告()
    {
        Item it = Fixtures.ValidBookmark(RootFolders.Toolbar) with { D = true, X = 0 };
        State s = Fixtures.StateOf(("b", it));

        ValidationResult r = s.Validate();
        Assert.True(r.IsValid, $"不应是硬错误：{r.Error}");
        Assert.Contains("缺少删除时间", string.Join(";", r.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_空state合法()
    {
        Assert.True(State.New().Validate().IsValid);
    }

    [Fact]
    public void Clone_是深拷贝()
    {
        State s = Fixtures.StateOf(("b", Fixtures.ValidBookmark(RootFolders.Toolbar)));
        State c = s.Clone();

        c.Items[Fixtures.KeyOf("b")] = Fixtures.ValidFolder(RootFolders.Menu);
        c.Items["new"] = Fixtures.ValidBookmark(RootFolders.Unfiled);

        Assert.Single(s.Items);
        Assert.Equal(ItemTypes.Bookmark, s.Items[Fixtures.KeyOf("b")].T);
    }

    [Fact]
    public void CountActive_墓碑不计入()
    {
        State s = Fixtures.StateOf(
            ("a", Fixtures.ValidBookmark(RootFolders.Toolbar)),
            ("b", Fixtures.ValidFolder(RootFolders.Menu)),
            ("c", Fixtures.Tomb(Fixtures.ValidBookmark(RootFolders.Unfiled), Fixtures.T0, 200, 0)));

        Assert.Equal(2, s.CountActive());
    }

    [Fact]
    public void KeysSorted_多次调用结果一致()
    {
        State s = State.New();
        for (int i = 0; i < 50; i++)
        {
            s.Items[Fixtures.KeyOf($"k{(char)('a' + (i % 26))}{(char)('a' + (i / 26))}")] =
                Fixtures.ValidBookmark(RootFolders.Toolbar);
        }

        string[] first = s.KeysSorted();
        for (int i = 0; i < 20; i++)
        {
            string[] got = s.KeysSorted();
            Assert.Equal(first.Length, got.Length);
            Assert.Equal(first, got);
        }
    }

    [Fact]
    public void 根目录常量正确()
    {
        foreach (string id in new[]
                 {
                     RootFolders.Toolbar, RootFolders.Menu, RootFolders.Unfiled, RootFolders.Mobile,
                 })
        {
            Assert.True(RootFolders.IsRootFolder(id), $"\"{id}\" 应被识别为根目录");
        }

        Assert.False(RootFolders.IsRootFolder("some-folder-key"));
        Assert.False(RootFolders.IsRootFolder(null));

        // 根目录 id 长度必须与设计文档一致（Firefox Places 的固定 id）
        foreach (string id in new[]
                 {
                     RootFolders.Toolbar, RootFolders.Menu, RootFolders.Unfiled, RootFolders.Mobile,
                 })
        {
            Assert.Equal(12, id.Length);
        }
    }

    [Fact]
    public void SameContent_忽略时间戳与墓碑位()
    {
        Item a = Fixtures.ValidBookmark(RootFolders.Toolbar);
        Item b = a with
        {
            M = Hlc.Encode(999, 9),
            A = Hlc.Encode(888, 8),
            D = true,
            X = 12345,
        };

        Assert.True(
            Merger.SameContent(a, b),
            "SameContent 不应比较 M/A/D/X —— 两端各自重新保存一次不应被当成冲突");

        Item c = a with { N = "别的标题" };
        Assert.False(Merger.SameContent(a, c));
    }
}
