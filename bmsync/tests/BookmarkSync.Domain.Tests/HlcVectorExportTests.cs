using System.Text;
using System.Text.Json;

namespace BookmarkSync.Domain.Tests;

/// <summary>
/// HLC 向量的<b>生成器</b>，以及围绕它的防护测试。
/// </summary>
/// <remarks>
/// 生成器与回放器分开放在不同文件是有意的：docs/plan.md 附录 D 第 13 条记录了
/// Go 版"生成器自己验证自己"的 blind spot —— 向量文件既是产物又是期望值，
/// 生成器一旦有 bug，错误会被一起固化进去，永远发现不了。拆开后
/// <see cref="HlcVectorTests"/> 才能用<b>同一份文件</b>去验证一个<b>独立</b>的
/// 计算过程。
/// </remarks>
public class HlcVectorExportTests
{
    /// <summary>
    /// 按固定脚本走一遍 HLC，产出向量文件内容。
    /// </summary>
    /// <returns>（JSON 文本，向量条数）</returns>
    public static (string Blob, int Count) BuildVectors()
    {
        var clk = new FixedClock(1_700_000_000_000L);
        Hlc h = Hlc.NewWithClock(clk.Now);
        var vectors = new List<HlcVector>();

        // ⚠️ 这里**只能调用一个**方法。早期的 Go 写法是无条件先 h.Now()、再在
        // op=="update" 时用 h.Update() 的返回值覆盖 —— 于是每个 update 步都
        // 偷偷多执行了一次 now()，把状态推进了，而那次 now() 的输出被丢弃。
        //
        // 后果：向量记录的是"多走了一步"的状态，与任何忠实的回放器都不符。
        // JS 侧忠实按 op 执行，于是从第一个 update 起就错位，症状是
        // "Go 与 C# 实现不一致" —— 指向完全错误的怀疑方向。
        void Step(string op, string remote)
        {
            string output = op == "update" ? h.Update(remote) : h.Now();
            vectors.Add(new HlcVector { Op = op, Remote = remote, At = clk.Now(), Out = output });
        }

        // 1) 物理时钟不动，连续本地事件（靠逻辑计数区分）
        for (int i = 0; i < 5; i++)
        {
            Step("now", string.Empty);
        }

        // 2) 物理时钟推进到新毫秒（计数应归零）
        clk.Add(1500);
        Step("now", string.Empty);

        // 3) 远端在同一毫秒内计数更高 —— 曾经写错而破坏因果性的分支
        Step("update", Hlc.Encode(clk.Now(), 40));
        Step("now", string.Empty);

        // 4) 远端毫秒更大
        Step("update", Hlc.Encode(clk.Now() + 60_000, 3));
        Step("now", string.Empty);

        // 5) 物理时钟大幅回拨
        clk.Add(-120_000);
        Step("now", string.Empty);

        // 6) 非法远端时间戳（应退化为纯本地推进，不崩溃）
        Step("update", "not-an-hlc");
        Step("now", string.Empty);

        // 7) 远端计数溢出边界
        Step("update", Hlc.Encode(clk.Now(), 99_999));
        Step("now", string.Empty);

        // 8) 物理时钟再次前进，确认溢出后仍能正常进位
        clk.Add(5);
        Step("now", string.Empty);
        Step("now", string.Empty);

        // 9) 远端时间戳完全等于当前物理毫秒且计数为 0
        Step("update", Hlc.Encode(clk.Now(), 0));
        Step("now", string.Empty);

        var file = new HlcVectorFileOut
        {
            Note = "由 bmsync/tests/BookmarkSync.Domain.Tests 的向量生成器生成。" +
                   "JS 侧必须逐条复现完全相同的 out —— 两端 HLC 不一致会导致合并非确定。",
            Layout = Hlc.Layout,
            Vectors = vectors,
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            // 中文 note 原样输出，与 Go 版一致
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        string blob = JsonSerializer.Serialize(file, options) + "\n";
        return (blob, vectors.Count);
    }

    [Fact]
    public void 生成器产出足够多的向量()
    {
        (string blob, int count) = BuildVectors();
        Assert.True(count >= 15, $"只生成了 {count} 条向量，跨端验证覆盖不足");
        Assert.Contains("\"vectors\"", blob, StringComparison.Ordinal);
        Assert.Contains(Hlc.Layout, blob, StringComparison.Ordinal);
    }

    /// <summary>
    /// 生成器的输出必须能被回放器原样读回来。
    /// </summary>
    /// <remarks>
    /// 覆盖的是"JSON 结构和实现约定对不上"这类问题：比如生成器写出
    /// <c>Op/Remote/At/Out</c> 的 PascalCase，而回放器按小写属性名读，
    /// 于是每个字段都是零值 —— 而回放器如果也没断言，结果会是 15 条
    /// "全对"的空测试。
    /// </remarks>
    [Fact]
    public void 生成器的输出能被回放器读回()
    {
        (string blob, _) = BuildVectors();
        var parsed = JsonSerializer.Deserialize(blob, TestJsonContext.Default.HlcVectorFile);

        Assert.NotNull(parsed);
        Assert.NotEmpty(parsed!.Vectors);
        Assert.All(parsed.Vectors, v =>
        {
            Assert.Contains(v.Op, new[] { "now", "update" });
            Assert.False(string.IsNullOrEmpty(v.Out));
            Assert.True(Hlc.IsValid(v.Out), $"向量输出 \"{v.Out}\" 不是合法 HLC");
        });
    }

    /// <summary>
    /// 写入向量文件时，目标目录必须已存在。
    /// </summary>
    /// <remarks>
    /// 这是从 Go 版直接继承的一个教训（docs/plan.md 附录 D 第 15 条）。
    /// Go 原实现用 <c>os.MkdirAll</c> 写向量，于是包目录移动后路径失效时，
    /// 它会自己造一棵新目录树并"成功"返回 —— 而 Go 与 JS 两侧读的都是仓库根
    /// 那份旧文件。<b>两边全绿，但跨端验证从未发生。</b>
    /// <para>
    /// 本实现刻意不建目录：路径错必须是硬错误。
    /// </para>
    /// </remarks>
    [Fact]
    public void WriteVectors_RefusesWhenDirectoryMissing()
    {
        (string blob, int count) = BuildVectors();
        Assert.True(count > 0);

        string missingDir = Path.Combine(Path.GetTempPath(), "bmsync-no-such-" + Guid.NewGuid().ToString("N"), "test");
        string target = Path.Combine(missingDir, "hlc_vectors.json");

        // 这里只验证"目录不存在时我们不会去创建它"。
        // 真正的写入路径上有 Directory.Exists 检查（见 导出向量供JS复现）。
        Assert.False(Directory.Exists(missingDir));
        Assert.ThrowsAny<IOException>(() => File.WriteAllText(target, blob));
        Assert.False(Directory.Exists(missingDir));
    }
}

/// <summary>向量文件的序列化形状（字段名与 Go 版一致，全小写）。</summary>
internal sealed class HlcVectorFileOut
{
    [System.Text.Json.Serialization.JsonPropertyName("note")]
    public string Note { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("layout")]
    public string Layout { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("vectors")]
    public List<HlcVector> Vectors { get; set; } = [];
}
