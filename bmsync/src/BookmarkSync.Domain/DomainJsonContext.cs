using System.Text.Json;
using System.Text.Json.Serialization;

namespace BookmarkSync.Domain;

/// <summary>
/// 领域类型的 JSON 源生成上下文。
/// </summary>
/// <remarks>
/// <para>
/// 为什么必须用源生成：<c>PublishAot=true</c>（为了 FROM scratch 镜像）会让
/// 运行时把反射式 JSON 整个关掉 —— 一旦用到反射，症状是运行时抛
/// <c>Reflection-based serialization has been disabled for this application</c>，
/// 而这个错误在编译期一个字都看不出来。
/// </para>
/// <para>
/// 源生成顺带给了协议层一个好处：哪些类型参与序列化写在代码里，
/// 改协议时会立刻看到编译错误，而不是运行时才发现某个字段悄悄没被序列化。
/// </para>
/// <para>
/// 注意 <see cref="Item"/> 与 <see cref="State"/> 用的是<b>自定义</b>转换器
/// （见 <see cref="ItemJsonConverter"/> / <see cref="StateJsonConverter"/>），
/// 它们在 <see cref="BmsyncJson"/> 的 <c>Converters</c> 里注册。转换器优先于
/// 源生成的元数据，所以这里的 <c>[JsonSerializable]</c> 只是为了让
/// 上下文知道这些类型存在。
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(State))]
[JsonSerializable(typeof(Item))]
[JsonSerializable(typeof(Conflict))]
[JsonSerializable(typeof(Summary))]
[JsonSerializable(typeof(List<Conflict>))]
internal partial class DomainJsonContext : JsonSerializerContext;

/// <summary>
/// AOT 安全的读写入口。
/// </summary>
/// <remarks>
/// <para>
/// 为什么不直接调 <c>JsonSerializer.Serialize(x, BmsyncJson.Storage)</c>：
/// 那两个泛型重载带 <c>[RequiresUnreferencedCode]</c> 与
/// <c>[RequiresDynamicCode]</c>，在 PublishAot 下会报 IL2026 / IL3050。
/// </para>
/// <para>
/// 而那些警告在这里是<b>误报</b>：泛型重载为了通用性做了保守假设，
/// 而我们连类型都写死了（<c>State</c>、<c>List&lt;Conflict&gt;</c>），
/// 并且真正写字段的是手写转换器 —— 不需要任何运行时代码生成。
/// </para>
/// <para>
/// 用 <see cref="JsonTypeInfo{T}"/> 重载正好绕开这个假设：它拿到的是
/// 源生成器在编译期产出的类型信息，AOT 能静态分析。
/// <b>不能靠 NoWarn 压掉</b> —— 那会让真正的 AOT 不兼容问题
/// （比如哪天有人写了匿名类型）也一起被吞掉，然后只在发布镜像时炸。
/// </para>
/// </remarks>
public static class DomainJson
{
    /// <summary>把 state 序列化成 UTF-8 字节（落盘用）。</summary>
    public static byte[] SerializeToUtf8Bytes(State s) =>
        JsonSerializer.SerializeToUtf8Bytes(s, DomainJsonContext.Default.State);

    /// <summary>把 state 序列化成字符串。</summary>
    public static string Serialize(State s) =>
        JsonSerializer.Serialize(s, DomainJsonContext.Default.State);

    /// <summary>解析 state（读盘用，宽容：忽略未知字段）。</summary>
    public static State? DeserializeState(string json) =>
        JsonSerializer.Deserialize(json, DomainJsonContext.Default.State);

    /// <summary>把冲突列表序列化成 UTF-8 字节（落盘用）。</summary>
    public static byte[] SerializeToUtf8Bytes(List<Conflict> conflicts) =>
        JsonSerializer.SerializeToUtf8Bytes(conflicts, DomainJsonContext.Default.ListConflict);

    /// <summary>解析冲突列表（读盘用）。</summary>
    public static List<Conflict>? DeserializeConflicts(string json) =>
        JsonSerializer.Deserialize(json, DomainJsonContext.Default.ListConflict);

    /// <summary>把 item 序列化成字符串（测试与调试用）。</summary>
    public static string SerializeItem(Item item) =>
        JsonSerializer.Serialize(item, DomainJsonContext.Default.Item);

    /// <summary>解析单个 item。</summary>
    public static Item DeserializeItem(string json) =>
        JsonSerializer.Deserialize(json, DomainJsonContext.Default.Item);
}
