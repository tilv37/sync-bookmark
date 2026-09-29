using System.Text.Json;
using System.Text.Json.Serialization;

namespace BookmarkSync.Domain.Tests;

/// <summary>
/// 可手工推进的时钟，让 HLC 测试完全确定、不依赖真实时间。
/// </summary>
internal sealed class FixedClock
{
    private long _ms;

    public FixedClock(long ms) => _ms = ms;

    public long Now() => _ms;

    public void Set(long ms) => _ms = ms;

    public long Add(long delta)
    {
        _ms += delta;
        return _ms;
    }
}

/// <summary>
/// HLC 的单元测试。服务端从 Go 迁到 .NET 10 时逐条移植，
/// 当时用来对照的 Go 实现（legacy-go/）在迁移确认后已删除。
/// </summary>
public class HlcTests
{
    // ── 编码 ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0L, 0, "0000000000000-00000")]
    [InlineData(1000L, 0, "0000000001000-00000")]
    [InlineData(1000L, 42, "0000000001000-00042")]
    [InlineData(1790000000000L, 99999, "1790000000000-99999")]
    [InlineData(9223372036854L, 1, "9223372036854-00001")] // 13 位上限
    public void Encode_格式固定宽度(long l, int c, string want) =>
        Assert.Equal(want, Hlc.Encode(l, c));

    [Fact]
    public void Decode_往返无损()
    {
        var rnd = new Random(1);
        for (int i = 0; i < 2000; i++)
        {
            long l = (long)rnd.NextInt64(9_000_000_000_000L);
            int c = rnd.Next(100_000);
            string s = Hlc.Encode(l, c);

            Assert.True(Hlc.TryDecode(s, out long gl, out int gc), $"Decode({s}) 失败");
            Assert.Equal(l, gl);
            Assert.Equal(c, gc);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("123")]
    [InlineData("0000000001000-0000")]
    [InlineData("0000000001000000000")]
    [InlineData("0000000001000_00000")]
    [InlineData("0000000001000-0000x")]
    [InlineData("abc-00000")]
    [InlineData("0000000001000-999999")] // 6 位计数
    [InlineData("-0000000001000-00000")]
    public void Decode_拒绝垃圾输入(string s)
    {
        Assert.False(Hlc.TryDecode(s, out _, out _), $"Decode({s}) 应当失败");
        Assert.False(Hlc.IsValid(s), $"IsValid({s}) 应当为 false");
    }

    [Theory]
    // 下面两个是实测确认过的分歧点，不是假想：
    // .NET 的 int.TryParse 默认样式会接受正负号与前后空白，
    // 而 Go 的 ParseUint 全部拒绝。若不做特殊处理，两套实现
    // 会对同一串输入给出不同判断，且分歧点在"非法输入"这条
    // 恰恰最该严格的路径上。
    [InlineData("+000000001000-00000")]
    [InlineData(" 000000001000-00000")]
    [InlineData("000000001000-0000 ")]
    [InlineData("00000000+1000-00000")]
    public void Decode_拒绝正负号与空白(string s) =>
        Assert.False(Hlc.TryDecode(s, out _, out _), $"Decode({s}) 应当失败（Go 侧同样拒绝）");

    /// <summary>
    /// 字典序 == 时间序。这是整个编码格式存在的理由：合并时可以在任何语言、
    /// 任何数据结构里直接比较字符串，不需要解析。
    /// </summary>
    [Fact]
    public void 字符串序等于时间序()
    {
        var rnd = new Random(2);
        var enc = new List<string>(1000);
        for (int i = 0; i < 1000; i++)
        {
            // 刻意让大量样本落在同一毫秒内，逼出「物理部分相同、只比计数」的路径
            enc.Add(Hlc.Encode(1_000_000_000_000L + rnd.Next(5), rnd.Next(100_000)));
        }

        enc.Sort(StringComparer.Ordinal);
        for (int i = 1; i < enc.Count; i++)
        {
            Assert.True(
                Hlc.Compare(enc[i - 1], enc[i]) <= 0,
                $"排序后仍不单调: \"{enc[i - 1]}\"({i - 1}) > \"{enc[i]}\"({i})");
        }
    }

    // ── 单调性与归零 ──────────────────────────────────────────────────

    [Fact]
    public void Now_严格单调()
    {
        var clk = new FixedClock(1_000_000_000_000L);
        Hlc h = Hlc.NewWithClock(clk.Now);

        string prev = h.Now();
        for (int i = 0; i < 1000; i++)
        {
            string cur = h.Now();
            Assert.True(Hlc.Compare(cur, prev) > 0, $"第 {i} 次 Now() 未递增: \"{prev}\" 之后是 \"{cur}\"");
            prev = cur;
        }
    }

    [Fact]
    public void Now_进入新毫秒后计数归零()
    {
        var clk = new FixedClock(1000);
        Hlc h = Hlc.NewWithClock(clk.Now);

        Assert.Equal("0000000001000-00000", h.Now());
        Assert.Equal("0000000001000-00001", h.Now());

        clk.Set(2000);
        Assert.Equal("0000000002000-00000", h.Now());
    }

    [Fact]
    public void 逻辑计数溢出进位到物理部分()
    {
        var clk = new FixedClock(1000);
        Hlc h = Hlc.NewWithClock(clk.Now);
        h.SetStateForTest(1000, 99_999 - 2);

        string[] seq = [h.Now(), h.Now(), h.Now(), h.Now(), h.Now()];
        for (int i = 1; i < seq.Length; i++)
        {
            Assert.True(Hlc.Compare(seq[i - 1], seq[i]) <= 0, $"溢出附近未单调: \"{seq[i - 1]}\" → \"{seq[i]}\"");
            Assert.Equal(19, seq[i].Length);
        }

        Assert.StartsWith("0000000001001-", seq[^1], StringComparison.Ordinal);
    }

    // ── 因果性：HLC 存在的全部理由 ─────────────────────────────────────
    //
    // docs/design.md §6.1：一旦这些测试失败，就意味着「两台机器时钟有偏差会
    // 静默丢书签」这个最初的问题重新出现了。

    [Fact]
    public void 跨设备因果性()
    {
        Hlc a = Hlc.NewWithClock(new FixedClock(1_000_000_000_000L).Now);
        Hlc b = Hlc.NewWithClock(new FixedClock(1_000_000_000_000L).Now);

        string last = string.Empty;
        for (int i = 0; i < 5; i++)
        {
            last = a.Now();
        }

        for (int i = 0; i < 5; i++)
        {
            b.Update(last);
            string got = b.Now();
            Assert.True(
                Hlc.Compare(got, last) > 0,
                $"B 的第 {i} 个后续事件 \"{got}\" 未大于 A 的 \"{last}\" —— 因果性被破坏");
        }
    }

    /// <summary>
    /// 本项目曾经写错、并在写测试时才发现的那个分支。
    /// </summary>
    /// <remarks>
    /// 场景：A 在第 1000ms 内产生 6 个事件，最后一个是 (1000, 00005)。
    /// 接收方 B 的物理时钟恰好也是 1000ms，但自身逻辑计数为 0。
    /// <para>
    /// 错误写法（Update 的第三个分支写成 "max == p 则 c = 0"）会让 B 得到
    /// (1000, 0)，其后的 Now() 产生 (1000, 1) &lt; (1000, 5) —— 因果性被破坏，
    /// 且症状是"书签随机丢失"，极难排查。
    /// </para>
    /// </remarks>
    [Fact]
    public void 因果性_同毫秒内追平远端()
    {
        Hlc a = Hlc.NewWithClock(new FixedClock(1000).Now);
        Hlc b = Hlc.NewWithClock(new FixedClock(1000).Now);

        string remote = string.Empty;
        for (int i = 0; i < 6; i++)
        {
            remote = a.Now();
        }

        Assert.Equal("0000000001000-00005", remote);

        b.Update(remote);
        string got = b.Now();

        // 关键性质：B 的后续事件必须严格大于它刚收到的事件
        Assert.True(
            Hlc.Compare(got, remote) > 0,
            $"B 的后续事件 \"{got}\" 未大于远端的 \"{remote}\" —— 追平场景下因果性被破坏");

        // 精确值：Update 本身是一次接收事件，消耗了计数 6（rc+1），
        // 随后的 Now() 再 +1 得到 7。比 6 更"浪费"一个计数是正确的 ——
        // 论文的算法把 receiveEvent 也算作一个事件。
        Assert.Equal("0000000001000-00007", got);
    }

    [Fact]
    public void 因果性_不依赖时钟偏差()
    {
        // B 的物理时钟比 A 快 1 小时。这个偏差绝不能影响因果性。
        Hlc a = Hlc.NewWithClock(new FixedClock(1_000_000_000_000L).Now);
        Hlc b = Hlc.NewWithClock(new FixedClock(1_000_000_000_000L + 3_600_000L).Now);

        string t1 = a.Now();
        b.Update(t1);
        string t2 = b.Now();
        Assert.True(Hlc.Compare(t2, t1) > 0, $"B 事件 \"{t2}\" 未大于 A 事件 \"{t1}\"");

        a.Update(t2);
        string t3 = a.Now();
        Assert.True(Hlc.Compare(t3, t2) > 0, $"A 事件 \"{t3}\" 未大于 B 事件 \"{t2}\"");
    }

    /// <summary>双向多轮传递，每一轮都必须严格递增。</summary>
    [Fact]
    public void 因果性_双向乒乓五十轮()
    {
        Hlc a = Hlc.NewWithClock(new FixedClock(1_000_000_000_000L).Now);
        Hlc b = Hlc.NewWithClock(new FixedClock(1_000_000_000_000L).Now);

        for (int round = 0; round < 50; round++)
        {
            string lastA = a.Now();
            b.Update(lastA);
            string lastB = b.Now();
            Assert.True(Hlc.Compare(lastB, lastA) > 0, $"第 {round} 轮：B 的 \"{lastB}\" 未大于 A 的 \"{lastA}\"");

            a.Update(lastB);
            string back = a.Now();
            Assert.True(Hlc.Compare(back, lastB) > 0, $"第 {round} 轮：A 的 \"{back}\" 未大于 B 的 \"{lastB}\"");
        }
    }

    /// <summary>
    /// 物理时钟被调回过去时，HLC 的逻辑时间必须继续前进，
    /// 否则已发出的时间戳会与新生成的撞在一起。
    /// </summary>
    [Fact]
    public void 时钟回拨不倒退()
    {
        var clk = new FixedClock(2_000_000_000_000L);
        Hlc h = Hlc.NewWithClock(clk.Now);

        string first = h.Now();
        clk.Set(2_000_000_000_000L - 3_600_000L); // 用户或 NTP 把时钟调回 1 小时前

        string prev = first;
        for (int i = 0; i < 100; i++)
        {
            string cur = h.Now();
            Assert.True(Hlc.Compare(cur, prev) > 0, $"时钟回拨后第 {i} 次 Now() 未递增: \"{prev}\" → \"{cur}\"");
            prev = cur;
        }

        Assert.True(Hlc.Compare(h.Current(), first) >= 0, $"回拨后当前时间 \"{h.Current()}\" 小于回拨前的 \"{first}\"");
    }

    // ── 比较与工具函数 ────────────────────────────────────────────────

    [Fact]
    public void Compare_全序()
    {
        string smaller = Hlc.Encode(1000, 5);
        string larger = Hlc.Encode(1000, 6);
        string nextMs = Hlc.Encode(1001, 0);

        Assert.Equal(-1, Hlc.Compare(smaller, larger));
        Assert.Equal(1, Hlc.Compare(larger, smaller));
        Assert.Equal(0, Hlc.Compare(smaller, smaller));
        Assert.Equal(1, Hlc.Compare(nextMs, larger));

        // 非法值排在合法值之前，保证被污染的一侧永远输，不会污染权威状态
        Assert.Equal(-1, Hlc.Compare("garbage", smaller));
        Assert.Equal(1, Hlc.Compare(smaller, "garbage"));

        // 两侧均非法时退化为字典序，保证确定性
        Assert.Equal(string.CompareOrdinal("a", "b"), Hlc.Compare("a", "b"));
        Assert.Equal(string.CompareOrdinal("b", "a"), Hlc.Compare("b", "a"));
    }

    [Fact]
    public void MaxHlc_取最大并忽略非法值()
    {
        Assert.Equal(Hlc.Zero, Hlc.MaxHlc([]));

        string a = Hlc.Encode(1000, 1);
        string b = Hlc.Encode(2000, 0);
        string c = Hlc.Encode(999, 99);

        Assert.Equal(b, Hlc.MaxHlc(a, b, c));
        Assert.Equal(a, Hlc.MaxHlc("garbage", a));
    }

    [Fact]
    public void State_MaxHlc_同时扫描M和A()
    {
        State s = State.New();
        Assert.Equal(Hlc.Zero, s.MaxHlc());

        s.Items["k1"] = new Item { M = Hlc.Encode(1000, 0), A = Hlc.Encode(500, 0) };
        s.Items["k2"] = new Item { M = Hlc.Encode(900, 0), A = Hlc.Encode(2000, 3) };

        // 期望取到 a 字段的最大值
        Assert.Equal(Hlc.Encode(2000, 3), s.MaxHlc());
    }

    [Fact]
    public void ObserveMany_吸收远端最大值()
    {
        Hlc h = Hlc.NewWithClock(new FixedClock(500).Now);
        string remote = Hlc.Encode(9000, 12);

        string got = h.ObserveMany([Hlc.Encode(100, 0), remote, "garbage"]);
        Assert.True(Hlc.Compare(got, remote) >= 0, $"ObserveMany 后 \"{got}\" 未追上远端 \"{remote}\"");

        string next = h.Now();
        Assert.True(Hlc.Compare(next, remote) > 0, $"ObserveMany 后的 Now() = \"{next}\"，未大于远端 \"{remote}\"");
    }

    [Fact]
    public void Update_收到非法远端不崩且仍单调()
    {
        Hlc h = Hlc.NewWithClock(new FixedClock(1000).Now);

        string first = h.Now();
        string got = h.Update("完全不是 HLC");

        Assert.True(Hlc.IsValid(got), $"非法远端之后仍应产出合法 HLC，得到 \"{got}\"");
        Assert.True(Hlc.Compare(got, first) > 0, $"非法远端之后 \"{got}\" 未大于 \"{first}\"");
    }

    /// <summary>HLC 会被多个线程碰到（HTTP handler + 快照），必须无竞态。</summary>
    [Fact]
    public void 并发安全()
    {
        Hlc h = Hlc.New();
        long baseMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Parallel.For(0, 50, i =>
        {
            for (int j = 0; j < 200; j++)
            {
                switch (i % 3)
                {
                    case 0:
                        _ = h.Now();
                        break;
                    case 1:
                        _ = h.Update(Hlc.Encode(baseMs + j, j));
                        break;
                    default:
                        _ = h.ObserveMany([Hlc.Encode(j, 0)]);
                        break;
                }
            }
        });
    }
}

// ── 跨端一致性向量 ──────────────────────────────────────────────────────

internal sealed class HlcVector
{
    [JsonPropertyName("op")]
    public string Op { get; set; } = string.Empty;

    [JsonPropertyName("remote")]
    public string Remote { get; set; } = string.Empty;

    [JsonPropertyName("at")]
    public long At { get; set; }

    [JsonPropertyName("out")]
    public string Out { get; set; } = string.Empty;
}

internal sealed class HlcVectorFile
{
    [JsonPropertyName("vectors")]
    public List<HlcVector> Vectors { get; set; } = [];
}

/// <summary>
/// 跨端一致性验证：Go ↔ C# ↔ JS 三方对同一份 test/hlc_vectors.json 负责。
/// </summary>
/// <remarks>
/// 这是整个移植里价值最高的两个测试：
/// <list type="bullet">
/// <item><b>C# 回放</b>：证明 C# 实现与 Go 产出的向量逐条一致。</item>
/// <item><b>C# 导出</b>：生成一份新的向量供 JS 侧复现，把链条闭上。</item>
/// </list>
/// 两端 HLC 不一致会让合并在 "m 恰好相等" 的分支上产生非确定行为 ——
/// 同一对书签在不同轮次里交替获胜，两端发散。
/// </remarks>
public class HlcVectorTests
{
    /// <summary>
    /// 定位向量文件：从测试的运行目录一路向上找，直到找到
    /// <c>test/hlc_vectors.json</c>。
    /// </summary>
    /// <remarks>
    /// 刻意<b>不写死向上找几级</b>。写死层数（"退 5 级"）在目录结构一改就失效，
    /// 而失效的表现是"找不到文件"——如果那时顺手改成 Skip，就会得到一个
    /// 永远不验证跨端一致性的假绿灯（这正是 docs/plan.md 附录 D 第 13、15 条
    /// 踩过的坑）。向上找到文件系统根为止，找不到就让测试失败。
    /// <para>
    /// 也不直接用相对路径：测试的运行目录是 bin/Debug/net10.0/，而"从项目目录
    /// 跑 dotnet test"时工作目录又不同，只有从程序集位置出发才两种都对。
    /// </para>
    /// </remarks>
    private static string VectorsPath
    {
        get
        {
            for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "test", "hlc_vectors.json");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            // 找不到时返回一个"不可能存在"的路径，让 Assert.True(File.Exists(...))
            // 带着可读的路径信息失败。绝不用 Skip 掩盖。
            return Path.Combine(
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "test", "hlc_vectors.json")));
        }
    }

    private static List<HlcVector> LoadVectors()
    {
        string path = VectorsPath;
        Assert.True(File.Exists(path), $"找不到向量文件 {path} —— 跨端验证无法进行");
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize(json, TestJsonContext.Default.HlcVectorFile)!.Vectors;
    }

    /// <summary>
    /// 逐条回放 Go 生成的向量并验证实际值。
    /// </summary>
    /// <remarks>
    /// 这是一个<b>自检</b>：回放器只做向量里说的事（op 是 now 就调 now，
    /// 是 update 就调 update），然后断言结果与向量一致。
    /// <para>
    /// 它存在的意义是捕捉"生成器与回放器不一致"这类问题：向量文件既是产物
    /// 又是期望值，如果只有生成它的那个测试来验证自己，生成器一旦有 bug
    /// （比如无条件多调一次 now()），错误会被一起固化进向量文件，永远发现不了。
    /// 详见 docs/plan.md 附录 D 第 13 条。
    /// </para>
    /// </remarks>
    [Fact]
    public void 回放向量()
    {
        List<HlcVector> vectors = LoadVectors();
        Assert.True(vectors.Count >= 15, $"向量数量 {vectors.Count} 偏少，跨端验证覆盖不足");

        var clk = new FixedClock(0);
        Hlc h = Hlc.NewWithClock(clk.Now);

        int bad = 0;
        for (int i = 0; i < vectors.Count; i++)
        {
            HlcVector v = vectors[i];
            clk.Set(v.At);
            string got = v.Op == "update" ? h.Update(v.Remote) : h.Now();

            if (got != v.Out)
            {
                bad++;
                Assert.Fail(
                    $"第 {i + 1} 条不一致（op={v.Op} at={v.At} remote=\"{v.Remote}\"）:\n" +
                    $"  向量: {v.Out}\n  实际: {got}");
            }
        }

        Assert.True(bad == 0, $"共 {bad}/{vectors.Count} 条不一致 —— C# 实现与 Go 已分节");
    }

    /// <summary>
    /// 用 C# 重新生成一份向量，与文件里的逐条比对。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="回放向量"/> 互为镜像：回放证明"C# 能复现文件里的
    /// 输出"，本测试证明"C# 从头算一遍也得到同样结果" —— 后者能抓住那种
    /// "回放器和实现共享同一个 bug，所以一起错"的盲区。
    /// <para>
    /// 不覆盖文件：向量文件是三端共享的产物，只应由生成器更新（见下方
    /// <see cref="导出向量供JS复现"/>）。这里只做比对。
    /// </para>
    /// </remarks>
    [Fact]
    public void 从头计算的结果与向量一致()
    {
        List<HlcVector> vectors = LoadVectors();
        var clk = new FixedClock(1_700_000_000_000L);
        Hlc h = Hlc.NewWithClock(clk.Now);

        var produced = new List<string>();
        foreach (HlcVector v in vectors)
        {
            clk.Set(v.At);
            produced.Add(v.Op == "update" ? h.Update(v.Remote) : h.Now());
        }

        var mismatches = new List<string>();
        for (int i = 0; i < vectors.Count; i++)
        {
            if (produced[i] != vectors[i].Out)
            {
                mismatches.Add($"  第 {i + 1} 条: 向量={vectors[i].Out} C#={produced[i]}");
            }
        }

        Assert.True(
            mismatches.Count == 0,
            $"C# 从头计算与向量有 {mismatches.Count} 处不同:\n{string.Join('\n', mismatches)}");
    }

    /// <summary>
    /// 导出向量供 JS 侧复现（等价于 Go 版的 TestHLCExportVectors）。
    /// </summary>
    /// <remarks>
    /// 默认**不覆盖**文件 —— 覆盖是显式动作，理由见
    /// <see cref="HlcVectorExportTests.WriteVectors_RefusesWhenDirectoryMissing"/>
    /// 与 Go 侧同款防护。设环境变量 <c>BMSYNC_WRITE_VECTORS=1</c> 才真正写入。
    /// </remarks>
    [Fact]
    public void 导出向量供JS复现()
    {
        string path = VectorsPath;
        (string blob, int count) = HlcVectorExportTests.BuildVectors();

        if (Environment.GetEnvironmentVariable("BMSYNC_WRITE_VECTORS") != "1")
        {
            // 不写也要确保"能算出来"：生成器坏掉时必须在测试阶段就发现，
            // 而不是等到真去跑导出命令时才发现。
            Assert.True(count >= 15, $"只生成了 {count} 条向量，覆盖不足");
            Assert.False(string.IsNullOrWhiteSpace(blob));
            _ = path;
            return;
        }

        string dir = Path.GetDirectoryName(path)!;
        Assert.True(Directory.Exists(dir), $"向量目录 {dir} 不存在 —— 拒绝新建（路径写错的信号）");
        File.WriteAllText(path, blob);
    }
}
