using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BookmarkSync.Domain;

/// <summary>
/// 全项目的 JSON 设置。
/// </summary>
/// <remarks>
/// <para>
/// 三处设置都带着"为什么"，因为它们每一个都会改变与 Go 版的字节输出，
/// 而"为什么这么写"比"写了什么"更容易在后续重构里丢失：
/// </para>
/// <list type="number">
/// <item>
/// <c>UnsafeRelaxedJsonEscaping</c>：.NET 默认把中文字符转成 <c>中</c>，
/// Go 原样输出 UTF-8。书签标题大量含中文，默认设置会让 5000 条书签的请求体
/// 从约 800KB 涨到 4MB 以上，直接撞上 nginx 的 <c>client_max_body_size</c>。
/// <para>
/// 为什么用"Unsafe"这个名字的编码器是安全的：本服务只提供 JSON API，
/// 响应不会被任何浏览器页面直接内联到 HTML 里，不存在 HTML 注入场景。
/// 真要内联，那该改的是"不要内联"，不是"转义所有中文"。
/// </para>
/// </item>
/// <item>
/// <c>Item</c> / <c>State</c> 用自定义转换器：见
/// <see cref="ItemJsonConverter"/> 与 <see cref="StateJsonConverter"/>。
/// </item>
/// <item>
/// <c>UnmappedMemberHandling = Disallow</c>：字段名拼错的客户端应该立刻拿到
/// 明确错误，而不是静默忽略后行为诡异（比如把 token 拼成 tokne，导致服务
/// 端一直返回 401 而没人想到是拼写问题）。
/// <para>
/// <b>但只用于解析客户端请求。</b>解析自己写出的 state.json 时必须关闭 ——
/// 它是持久化格式，将来加了字段后旧版本读到新文件不该崩。
/// </para>
/// </item>
/// </list>
/// </remarks>
public static class BmsyncJson
{
    /// <summary>写 state.json / 冲突记录用：宽松、key 有序、可省略空字段。</summary>
    public static readonly JsonSerializerOptions Storage = Create(disableUnknownFields: false);

    /// <summary>解析客户端请求用：未知字段视为错误。</summary>
    public static readonly JsonSerializerOptions StrictRequest = Create(disableUnknownFields: true);

    private static JsonSerializerOptions Create(bool disableUnknownFields) => new()
    {
        // 源生成上下文：PublishAot 会禁用反射式 JSON，见 DomainJsonContext 的说明。
        TypeInfoResolver = DomainJsonContext.Default,

        // 见上方第 1 条
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,

        // 见上方第 2 条
        Converters =
        {
            new ItemJsonConverter(),
            new StateJsonConverter(),
        },

        // 见上方第 3 条
        UnmappedMemberHandling = disableUnknownFields
            ? JsonUnmappedMemberHandling.Disallow
            : JsonUnmappedMemberHandling.Skip,

        // 缩进关闭：state.json 是全量同步的载荷，缩进会白白撑大三到五倍
        WriteIndented = false,

        // 让大整数/浮点不被改写格式。书签数据里没有浮点，但保持严格
        // 总比"某天不小心引入一个 double 然后精度悄悄变了"要好。
        NumberHandling = JsonNumberHandling.Strict,
    };
}
