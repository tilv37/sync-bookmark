using System.Text.Json;

namespace BookmarkSync.Domain.Tests;

/// <summary>
/// JSON 编码的跨语言一致性测试。
/// </summary>
/// <remarks>
/// 这个文件里的每一条断言都在钉一个"Go 与 C# 曾经会不一致"的点。
/// 不一致不会让程序崩 —— 它只是让体积悄悄涨三倍、或者让文件顺序变得随机，
/// 那类问题在真机上表现为"同步变慢了""git diff 全是噪声"，极难归因。
/// <para>
/// 真正的跨实现逐字节比对在 test/parity.sh 里做（需要 Go 工具链），
/// 这里的用例保证即使没有 Go 工具链，这些性质也被守住。
/// </para>
/// </remarks>
public class JsonParityTests
{
    private static string Serialize(State s) => JsonSerializer.Serialize(s, BmsyncJson.Storage);

    [Fact]
    public void 字段顺序与Go一致()
    {
        State s = State.New();
        s.Items[K] = Fixtures.Bookmark(RootFolders.Toolbar, "标题", "https://example.com", 100, 0);

        // Go 的结构体声明顺序：p t n u a m d x
        Assert.Equal(
            """{"v":1,"items":{"aaaa":{"p":"toolbar_____","t":"b","n":"标题","u":"https://example.com","a":"0000000000100-00000","m":"0000000000100-00000"}}}""".Replace("aaaa", Fixtures.KeyOf("b")),
            Serialize(s));
    }

    [Fact]
    public void 空字符串被省略()
    {
        // 文件夹没有 url，Go 的 omitempty 会省略 u 字段。
        // C# 的 JsonIgnoreCondition.WhenWritingDefault **不会**（实测确认），
        // 所以必须由自定义转换器保证。
        State s = State.New();
        s.Items[K] = Fixtures.Folder(RootFolders.Toolbar, "目录", 100, 0);

        string json = Serialize(s);
        Assert.DoesNotContain("\"u\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"d\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"x\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void 墓碑写出d和x()
    {
        State s = State.New();
        s.Items[K] = Fixtures.Tomb(
            Fixtures.Bookmark(RootFolders.Toolbar, "x", "https://x.example", 100, 0), 1_700_000_000_000L, 200, 0);

        string json = Serialize(s);
        Assert.Contains("\"d\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"x\":1700000000000", json, StringComparison.Ordinal);
    }

    [Fact]
    public void 中文原样输出不做Unicode转义()
    {
        // 这是体积问题：默认的 JavaScriptEncoder.Default 会把中文写成 \uXXXX，
        // 5000 条书签的请求体会从约 800KB 涨到 4MB 以上，直接撞上
        // nginx 的 client_max_body_size。
        State s = State.New();
        s.Items[K] = Fixtures.Bookmark(RootFolders.Toolbar, "中文标题 · & <tag>", "https://x.example", 100, 0);

        string json = Serialize(s);

        Assert.Contains("中文标题", json, StringComparison.Ordinal);

        // 中日韩统一表意文字与常用符号一个都不该出现 \u 转义
        Assert.DoesNotContain("\\u4E", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\\u00B7", json, StringComparison.OrdinalIgnoreCase);

        // HTML 敏感字符也必须原样 —— Go 的 encoding/json 默认会把它们转成
        // <，UnsafeRelaxedJsonEscaping 刻意不转义。这是一处**有意**
        // 不对齐：两者都能被 JSON.parse 正确解析，而本服务不把响应内联进
        // HTML 页面，所以"防 HTML 注入"在这里没有收益，只有体积损失。
        Assert.Contains("& <tag>", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// 钉住一处<b>已知且刻意接受</b>的分歧：非 BMP 字符（emoji）的转义。
    /// </summary>
    /// <remarks>
    /// Go 的 encoding/json 不转义任何非 ASCII 字符（U+2028/U+2029 除外），
    /// 所以 emoji 是 4 字节原样输出。.NET 即使设成 UnsafeRelaxedJsonEscaping，
    /// 仍会把非 BMP 字符（代理对）写成 <c>\uD83C\uDFAF</c>（12 字节）。
    /// <para>
    /// 实测确认过另一条路走不通：<c>JavaScriptEncoder.Create(UnicodeRanges.All)</c>
    /// 会把 <c>&amp; &lt; &gt; "</c> 也一并转义，反而离 Go 更远。
    /// </para>
    /// <para>
    /// 影响：一个带 emoji 的标题多 8 字节。相比"中文被转义"带来的 6 倍体积差，
    /// 两者不在一个量级。要严格对齐就得自己实现 <see cref="Text.Encodings.Web.JavaScriptEncoder"/>，
    /// 那是拿一个真实的安全面风险换几十字节，不划算。
    /// </para>
    /// <para>
    /// 这条测试的作用是<b>钉住现状</b>：哪天 SDK 改了行为导致中文又被转义，
    /// 或者将来有人"顺手优化"成 UnicodeRanges.All，这里会立刻响。
    /// </para>
    /// </remarks>
    [Fact]
    public void 非BMP字符的已知分歧_emoji()
    {
        State s = State.New();
        s.Items[K] = Fixtures.Bookmark(RootFolders.Toolbar, "目标 🎯", "https://x.example", 100, 0);

        string json = Serialize(s);

        // 中文部分必须原样
        Assert.Contains("目标", json, StringComparison.Ordinal);

        // emoji 部分：记录当前实际行为。两边都能被 JSON.parse 正确解析，
        // 所以"内容保住了"是可验证的；字节数差异是已知并接受的代价。
        Assert.True(
            json.Contains("🎯", StringComparison.Ordinal) || json.Contains("\\uD83C\\uDFAF", StringComparison.Ordinal),
            "emoji 要么原样输出、要么以代理对转义，不能凭空丢失");

        // 往返之后内容必须还原 —— 这才是"分歧无害"的真正依据
        State back = JsonSerializer.Deserialize<State>(json, BmsyncJson.Storage)!;
        Assert.Equal("目标 🎯", back.Items[K].N);
    }

    [Fact]
    public void items的key按字典序输出()
    {
        // Go 的 encoding/json 序列化 map 时排序输出；.NET 的 Dictionary 按插入顺序。
        // 不对齐的后果不是解析出错，而是 state.json 每次重写顺序都不同 ——
        // git diff 满屏噪声，且 Go 与 C# 两份实现无法逐字节对照。
        State s = State.New();
        for (int i = 0; i < 10; i++)
        {
            s.Items[Fixtures.KeyOf($"k{i}")] = Fixtures.Bookmark(RootFolders.Toolbar, "t", "https://x.example", 100, 0);
        }

        string[] writtenOrder = ExtractItemKeys(Serialize(s));
        string[] expected = s.KeysSorted();

        Assert.Equal(expected, writtenOrder);
    }

    [Fact]
    public void 相同内容无论插入顺序都产出相同字节()
    {
        State a = State.New();
        State b = State.New();
        for (int i = 0; i < 10; i++)
        {
            a.Items[Fixtures.KeyOf($"k{i}")] = Fixtures.Bookmark(RootFolders.Toolbar, "t", "https://x.example", 100, 0);
        }

        for (int i = 9; i >= 0; i--)
        {
            b.Items[Fixtures.KeyOf($"k{i}")] = Fixtures.Bookmark(RootFolders.Toolbar, "t", "https://x.example", 100, 0);
        }

        Assert.Equal(Serialize(a), Serialize(b));
    }

    [Fact]
    public void 往返不丢字段()
    {
        // State 的属性都是 init-only，必须在对象初始化器里一次性给全 ——
        // 这是刻意的（state 一旦参与合并就不该被就地改），代价是构造语法啰嗦一点。
        Item tomb = Fixtures.Tomb(
            Fixtures.Bookmark(RootFolders.Toolbar, "中文标题", "https://x.example", 100, 0), 1_700_000_000_000L, 200, 7);

        var items = State.NewItemsDictionary();
        items[K] = tomb;
        State original = new() { Clock = "1700000000000-00042", Items = items };

        State back = JsonSerializer.Deserialize<State>(Serialize(original), BmsyncJson.Storage)!;

        Assert.Equal(original.Clock, back.Clock);
        Assert.Equal(original.Items[K], back.Items[K]);
    }

    [Fact]
    public void 容忍缺失字段()
    {
        // 客户端漏发字段不该让整份上传被拒 —— 硬错误应该留给"格式非法"
        // （见 State.Validate）而不是"字段没来"。
        const string json = """{"v":1,"items":{"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa":{"n":"只有标题"}}}""";
        State s = JsonSerializer.Deserialize<State>(json, BmsyncJson.Storage)!;

        Assert.Equal("只有标题", s.Items["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"].N);
        Assert.Equal(string.Empty, s.Items["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"].M);
    }

    [Fact]
    public void 读取时忽略未知字段()
    {
        // state.json 是持久化格式：将来加了字段后旧版本读到新文件不该崩。
        const string json =
            """{"v":1,"futureField":123,"items":{"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa":{"n":"x","m":"0000000000100-00000","z":true}}}""";

        State s = JsonSerializer.Deserialize<State>(json, BmsyncJson.Storage)!;
        Assert.Equal("x", s.Items["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"].N);
    }

    [Fact]
    public void 严格模式下未知字段报错()
    {
        // 请求侧相反：字段名拼错要立刻暴露，而不是静默忽略后行为诡异。
        const string json = """{"v":1,"items":{},"devcie":"typo"}""";

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<State>(json, BmsyncJson.StrictRequest));
    }

    private static string[] ExtractItemKeys(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement items = doc.RootElement.GetProperty("items");

        return items.EnumerateObject().Select(p => p.Name).ToArray();
    }

    private static readonly string K = Fixtures.KeyOf("b");
}
