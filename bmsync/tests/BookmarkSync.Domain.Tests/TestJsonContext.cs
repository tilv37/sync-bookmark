using System.Text.Json.Serialization;

namespace BookmarkSync.Domain.Tests;

/// <summary>
/// 测试自己用到的类型的 JSON 源生成上下文。
/// </summary>
/// <remarks>
/// 为什么需要：测试项目因为引用了 Domain 而继承了
/// <c>PublishAot</c> 带来的"反射式 JSON 被禁用"，于是
/// <c>JsonSerializer.Deserialize&lt;HlcVectorFile&gt;(json, BmsyncJson.Storage)</c>
/// 会在运行时抛 NotSupportedException —— 因为 <c>HlcVectorFile</c> 是测试私有的
/// 类型，<c>DomainJsonContext</c> 不认识它。
/// <para>
/// 症状有点误导：报错说"类型没被 TypeInfoResolver 提供"，读起来像是
/// 生产代码的上下文配漏了，实际上只是测试里多了个类型。
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(HlcVectorFile))]
[JsonSerializable(typeof(HlcVectorFileOut))]
[JsonSerializable(typeof(List<HlcVector>))]
internal partial class TestJsonContext : JsonSerializerContext;
