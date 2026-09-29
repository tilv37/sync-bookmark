using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using BookmarkSync.Domain;
using BookmarkSync.Store;

namespace BookmarkSync.Server;

/// <summary>
/// API 类型的 JSON 源生成上下文。
/// </summary>
/// <remarks>
/// 与 <see cref="DomainJsonContext"/> 同理：<c>PublishAot=true</c> 禁用反射式
/// JSON，所有参与序列化的类型都必须在这里登记。
/// <para>
/// 这里必须<b>重复</b>登记领域类型（State / Item / Conflict / Summary）：
/// 源生成的上下文各自独立，Server 的上下文不认 Domain 的。
/// 重复的是"类型名单"，不是实现。
/// </para>
/// </remarks>
// ⚠️ Encoder 必须显式写成 UnsafeRelaxedJsonEscaping。
//
// 只写"没有 Encoder"是不行的 —— 源生成器会把它**烘进**生成的类型信息里，
// 而那个默认值是 JavaScriptEncoder.Default：中文、&、< 全被转成 \uXXXX。
// 后果是"编译通过、测试大部分通过、接口返回 200"，只有那条断言
// "响应里的中文不转义" 会失败 —— 而它恰好是 140 个用例里最容易被人
// 当成"测试写得不对"而改掉的那一条。
//
// 为什么这个坑值得单独写一段：ApiJson.Response（运行时构造的 options）
// 里也设了同一个 Encoder，两处都需要。用一处、漏一处的症状是
// "落盘的 state.json 是对的，接口响应里的中文却被转义了" ——
// 同一个程序里两套转义规则，比统一转义更难查。
// ⚠️ 这里**不能**设 Encoder —— JsonSourceGenerationOptionsAttribute
// 并没有 Encoder 属性（实测反射确认：只有 WriteIndented / NumberHandling /
// Converters / UnmappedMemberHandling 等 21 个，无 Encoder）。
//
// 也不设 Encoder 的后果是：上下文自带的那套 options 用的是默认编码器，
// 中文、&、< 全被转成 \uXXXX。实测行为对照：
//
//   Ctx.Default.Dto  →  {"N":"\u4E2D\u6587 \u0026 \u003Ctag>"}   ← 转义了
//   opt+TypeInfo     →  {"N":"中文 & <tag>"}                    ← 没转义
//
// 也就是说：**用上下文自带的 type info 就会丢转义设置，
// 必须从运行时构造的 options 上取 type info。** 见下方 TypeInfoOf。
[JsonSourceGenerationOptions(
    WriteIndented = false,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(SyncRequest))]
[JsonSerializable(typeof(SyncResponse))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(ConflictsResponse))]
[JsonSerializable(typeof(HistoryResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(SnapshotInfo))]
[JsonSerializable(typeof(List<SnapshotInfo>))]
[JsonSerializable(typeof(State))]
[JsonSerializable(typeof(Item))]
[JsonSerializable(typeof(Conflict))]
[JsonSerializable(typeof(Summary))]
[JsonSerializable(typeof(List<Conflict>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class ApiJsonContext : JsonSerializerContext;

/// <summary>
/// API 层实际使用的 JSON 设置。
/// </summary>
/// <remarks>
/// <para>
/// 不能直接用 <see cref="ApiJsonContext"/> 的 <c>Default.Options</c>：源生成器
/// 会为 <see cref="State"/> 生成一套"标准"的元数据，<b>绕过</b>我们在
/// <c>Domain</c> 里写的 <see cref="ItemJsonConverter"/> / <see cref="StateJsonConverter"/>。
/// 那两个转换器才是 key 排序和空字段省略的保证 —— 丢了它们，
/// state.json 的 key 顺序会变成字典插入顺序，且空串字段会被写出来。
/// </para>
/// <para>
/// 这里把两个转换器显式加回 <c>Converters</c>：<c>JsonSerializerOptions.Converters</c>
/// 的优先级高于源生成的元数据。
/// </para>
/// <para>
/// <b>这个坑值得单独记一笔</b>：它的症状是"state.json 顺序乱了、体积大了"，
/// 而不是任何形式的报错 —— 编译通过、测试通过、接口照常返回 200。
/// 唯一能发现它的方式是拿两份输出做逐字节对比。
/// </para>
/// </remarks>
public static class ApiJson
{
    /// <summary>写响应用。</summary>
    public static readonly JsonSerializerOptions Response = Create(
        JsonUnmappedMemberHandling.Skip);

    /// <summary>解析客户端请求用：未知字段视为错误。</summary>
    public static readonly JsonSerializerOptions StrictRequest = Create(
        JsonUnmappedMemberHandling.Disallow);

    private static JsonSerializerOptions Create(JsonUnmappedMemberHandling handling) => new()
    {
        TypeInfoResolver = ApiJsonContext.Default,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        UnmappedMemberHandling = handling,
        NumberHandling = JsonNumberHandling.Strict,
        Converters =
        {
            new ItemJsonConverter(),
            new StateJsonConverter(),
        },
    };

    // ── AOT 安全的读写入口 ────────────────────────────────────────────
    //
    // 两个约束在这里交汇：
    //
    // 1. 不能用 JsonSerializer.Serialize(value, Response) 那个泛型重载 ——
    //    它带 [RequiresDynamicCode]，PublishAot 下报 IL3050。
    //    用 JsonTypeInfo<T> 重载正好绕开（而这里是误报：类型是写死的，
    //    写字段的又是手写转换器，不需要任何运行时代码生成）。
    //
    // 2. 不能用 ApiJsonContext.Default.XXX（上下文自带的 type info）——
    //    见文件顶部那段实测：它带着默认编码器，会把中文转义掉。
    //
    // 所以统一走 TypeInfoOf<T>()：从**运行时**的 options 上取 type info，
    // 那里同时有 TypeInfoResolver（源生成的元数据）与 Encoder（不转义）。
    //
    // 不能靠 NoWarn 压掉 IL3050 —— 那会让真正的 AOT 不兼容（比如哪天
    // 写了匿名类型）也一起被吞掉，然后只在发布镜像时才炸。

    /// <summary>
    /// 从运行时 options 上取 type info。**不要**改用
    /// <c>ApiJsonContext.Default.XXX</c>，见本文件顶部的实测说明。
    /// </summary>
    public static JsonTypeInfo<T> TypeInfoOf<T>() => (JsonTypeInfo<T>)Response.GetTypeInfo(typeof(T));

    /// <summary>
    /// 从<b>严格</b> options 上取 type info。解析客户端请求必须走这里。
    /// </summary>
    /// <remarks>
    /// 分成两个入口不是重复：<see cref="TypeInfoOf{T}"/> 用的是 <c>Response</c>
    /// （未知字段跳过，state.json 是持久化格式，加字段后旧版本读到新文件不该崩），
    /// 而请求侧必须 <see cref="JsonUnmappedMemberHandling.Disallow"/> ——
    /// 客户端把 <c>device</c> 拼成 <c>devcie</c> 时，字段名拼错应该立刻暴露，
    /// 而不是让服务端一直按默认设备处理而没人想到是拼写问题。
    /// <para>
    /// 实测确认：两者对 <c>{"devcie":"typo"}</c> 的行为确实不同
    /// （Skip 静默通过 / Disallow 抛 JsonException），所以这里必须分开。
    /// </para>
    /// </remarks>
    public static JsonTypeInfo<T> StrictTypeInfoOf<T>() =>
        (JsonTypeInfo<T>)StrictRequest.GetTypeInfo(typeof(T));

    public static SyncRequest? ParseRequest(ReadOnlySpan<byte> body) =>
        JsonSerializer.Deserialize(body, StrictTypeInfoOf<SyncRequest>());

    public static HealthResponse? ParseHealth(string body) =>
        JsonSerializer.Deserialize(body, TypeInfoOf<HealthResponse>());

    public static byte[] ToUtf8Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, TypeInfoOf<T>());

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, TypeInfoOf<T>());
}
