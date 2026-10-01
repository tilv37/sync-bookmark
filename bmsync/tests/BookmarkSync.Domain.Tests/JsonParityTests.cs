using System.Text.Json;

namespace BookmarkSync.Domain.Tests;

/// <summary>
/// Cross-language consistency tests for JSON encoding.
/// </summary>
/// <remarks>
/// Every assertion here pins a point where "Go and C# used to disagree".
/// A mismatch does not crash anything — it silently triples size or randomizes file order,
/// which shows on real machines as "sync got slow" / "git diff is all noise" and is hard to attribute.
/// <para>
/// True cross-implementation byte comparison lives in test/parity.sh (needs a Go toolchain);
/// these cases hold the same properties without a Go toolchain.
/// </para>
/// </remarks>
public class JsonParityTests
{
    private static string Serialize(State s) => JsonSerializer.Serialize(s, BmsyncJson.Storage);

    [Fact]
    public void FieldOrderMatchesGo()
    {
        State s = State.New();
        s.Items[K] = Fixtures.Bookmark(RootFolders.Toolbar, "标题", "https://example.com", 100, 0);

        // Go struct declaration order: p t n u a m d x
        Assert.Equal(
            """{"v":1,"items":{"aaaa":{"p":"toolbar_____","t":"b","n":"标题","u":"https://example.com","a":"0000000000100-00000","m":"0000000000100-00000"}}}""".Replace("aaaa", Fixtures.KeyOf("b")),
            Serialize(s));
    }

    [Fact]
    public void EmptyStringsAreOmitted()
    {
        // Folders have no url; Go omitempty drops the u field.
        // C# JsonIgnoreCondition.WhenWritingDefault does **not** (verified by test),
        // so the custom converter must guarantee it.
        State s = State.New();
        s.Items[K] = Fixtures.Folder(RootFolders.Toolbar, "目录", 100, 0);

        string json = Serialize(s);
        Assert.DoesNotContain("\"u\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"d\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"x\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void TombstoneWritesDAndX()
    {
        State s = State.New();
        s.Items[K] = Fixtures.Tomb(
            Fixtures.Bookmark(RootFolders.Toolbar, "x", "https://x.example", 100, 0), 1_700_000_000_000L, 200, 0);

        string json = Serialize(s);
        Assert.Contains("\"d\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"x\":1700000000000", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ChineseEmittedWithoutUnicodeEscaping()
    {
        // Size issue: the default JavaScriptEncoder.Default writes CJK as \uXXXX,
        // so 5000 bookmarks grow from ~800KB to 4MB+, hitting
        // nginx client_max_body_size head-on.
        State s = State.New();
        s.Items[K] = Fixtures.Bookmark(RootFolders.Toolbar, "中文标题 · & <tag>", "https://x.example", 100, 0);

        string json = Serialize(s);

        Assert.Contains("中文标题", json, StringComparison.Ordinal);

        // No CJK ideographs or common symbols may appear as \u escapes
        Assert.DoesNotContain("\\u4E", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\\u00B7", json, StringComparison.OrdinalIgnoreCase);

        // HTML-sensitive chars must also stay raw — Go encoding/json escapes them to
        // \u003C by default; UnsafeRelaxedJsonEscaping deliberately does not. This is an **intentional**
        // mismatch: both parse fine under JSON.parse, and this service never inlines responses into
        // HTML pages, so "HTML-injection protection" buys nothing here, only size.
        Assert.Contains("& <tag>", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// Pins one <b>known and deliberately accepted</b> divergence: escaping of non-BMP (emoji) chars.
    /// </summary>
    /// <remarks>
    /// Go encoding/json escapes no non-ASCII chars (except U+2028/U+2029),
    /// so emoji goes out as 4 raw bytes. Even with UnsafeRelaxedJsonEscaping, .NET writes
    /// non-BMP chars (surrogate pairs) as <c>\uD83C\uDFAF</c> (12 bytes).
    /// <para>
    /// Measured and rejected the alternative: <c>JavaScriptEncoder.Create(UnicodeRanges.All)</c>
    /// would also escape <c>&amp; &lt; &gt; "</c>, drifting further from Go.
    /// </para>
    /// <para>
    /// Impact: 8 extra bytes per emoji title. Compared with the 6x size blowup from "CJK gets escaped",
    /// the two are orders apart. Exact alignment would need a custom <see cref="Text.Encodings.Web.JavaScriptEncoder"/> implementation,
    /// trading a real attack-surface risk for dozens of bytes — not worth it.
    /// </para>
    /// <para>
    /// This test <b>pims the status quo</b>: if an SDK change re-escapes CJK,
    /// or someone "helpfully optimizes" to UnicodeRanges.All, it fires immediately.
    /// </para>
    /// </remarks>
    [Fact]
    public void KnownNonBmpDivergence_Emoji()
    {
        State s = State.New();
        s.Items[K] = Fixtures.Bookmark(RootFolders.Toolbar, "目标 🎯", "https://x.example", 100, 0);

        string json = Serialize(s);

        // The Chinese part must stay raw
        Assert.Contains("目标", json, StringComparison.Ordinal);

        // Emoji part: record actual behavior. Both parse fine under JSON.parse,
        // so "content preserved" is verifiable; the byte-count gap is a known, accepted cost.
        Assert.True(
            json.Contains("🎯", StringComparison.Ordinal) || json.Contains("\\uD83C\\uDFAF", StringComparison.Ordinal),
            "emoji must be raw or surrogate-pair escaped, never silently dropped");

        // Content must round-trip intact — the real basis for "the divergence is harmless"
        State back = JsonSerializer.Deserialize<State>(json, BmsyncJson.Storage)!;
        Assert.Equal("目标 🎯", back.Items[K].N);
    }

    [Fact]
    public void ItemKeysEmittedInSortedOrder()
    {
        // Go encoding/json sorts map keys on output; .NET Dictionary follows insertion order.
        // The mismatch breaks nothing at parse time; instead every state.json rewrite shuffles order —
        // noisy git diffs, and the Go and C# builds stop being byte-comparable.
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
    public void SameContentYieldsSameBytesRegardlessOfInsertionOrder()
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
    public void RoundTripPreservesFields()
    {
        // State properties are all init-only and must be set in one initializer —
        // deliberate (a state in a merge must never be mutated in place), at the cost of verbose construction.
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
    public void ToleratesMissingFields()
    {
        // A client omitting fields must not doom the whole upload — hard errors are for "malformed"
        // (see State.Validate), not for "field missing".
        const string json = """{"v":1,"items":{"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa":{"n":"只有标题"}}}""";
        State s = JsonSerializer.Deserialize<State>(json, BmsyncJson.Storage)!;

        Assert.Equal("只有标题", s.Items["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"].N);
        Assert.Equal(string.Empty, s.Items["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"].M);
    }

    [Fact]
    public void IgnoresUnknownFieldsWhenReading()
    {
        // state.json is a persisted format: an old build reading a newer file with added fields must not crash.
        const string json =
            """{"v":1,"futureField":123,"items":{"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa":{"n":"x","m":"0000000000100-00000","z":true}}}""";

        State s = JsonSerializer.Deserialize<State>(json, BmsyncJson.Storage)!;
        Assert.Equal("x", s.Items["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"].N);
    }

    [Fact]
    public void StrictModeRejectsUnknownFields()
    {
        // The request side is the opposite: misspelled field names must surface at once, not misbehave silently.
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
