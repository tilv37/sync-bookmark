using System.Text.Json.Serialization;

namespace BookmarkSync.Domain.Tests;

/// <summary>
/// Source-generated JSON context for types used by tests themselves.
/// </summary>
/// <remarks>
/// Why it exists: the test project references Domain and inherits
/// <c>PublishAot</c>'s "reflection-based JSON is disabled", so
/// <c>JsonSerializer.Deserialize&lt;HlcVectorFile&gt;(json, BmsyncJson.Storage)</c>
/// throws NotSupportedException at runtime — because <c>HlcVectorFile</c> is test-private
/// and <c>DomainJsonContext</c> does not know it.
/// <para>
/// The symptom misleads: "type not provided by the TypeInfoResolver" reads like
/// the production context missed a registration, when really the tests just added a type.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(HlcVectorFile))]
[JsonSerializable(typeof(HlcVectorFileOut))]
[JsonSerializable(typeof(List<HlcVector>))]
internal partial class TestJsonContext : JsonSerializerContext;
