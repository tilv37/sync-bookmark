using System.Text.Json;
using System.Text.Json.Serialization;

namespace BookmarkSync.Domain;

/// <summary>
/// Source-generated JSON context for domain types.
/// </summary>
/// <remarks>
/// <para>
/// Source generation is required because <c>PublishAot=true</c> (for the FROM scratch
/// image) disables reflection-based JSON entirely — using reflection throws
/// <c>Reflection-based serialization has been disabled for this application</c> at
/// runtime with no compile-time hint.
/// </para>
/// <para>
/// It also benefits the protocol layer: the serializable types are listed in code,
/// so protocol changes surface as compile errors instead of silently dropped fields.
/// </para>
/// <para>
/// <see cref="Item"/> and <see cref="State"/> use <b>custom</b> converters
/// (see <see cref="ItemJsonConverter"/> / <see cref="StateJsonConverter"/>),
/// registered in <see cref="BmsyncJson"/>'s <c>Converters</c>. Converters take
/// precedence over generated metadata, so the <c>[JsonSerializable]</c> entries here
/// only declare that these types exist.
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
/// AOT-safe read/write entry points.
/// </summary>
/// <remarks>
/// <para>
/// Not plain <c>JsonSerializer.Serialize(x, BmsyncJson.Storage)</c>: those generic
/// overloads carry <c>[RequiresUnreferencedCode]</c> and <c>[RequiresDynamicCode]</c>,
/// which fail PublishAot with IL2026 / IL3050.
/// </para>
/// <para>
/// Those warnings are <b>false positives</b> here: the generic overload assumes the
/// worst case, while our types are fixed (<c>State</c>, <c>List&lt;Conflict&gt;</c>)
/// and the fields are written by hand-written converters — no runtime codegen needed.
/// </para>
/// <para>
/// The <see cref="JsonTypeInfo{T}"/> overloads sidestep that assumption: they take
/// compile-time type info from source generation, which AOT can analyze statically.
/// <b>Do not suppress with NoWarn</b> — that would also swallow genuine AOT
/// incompatibilities (e.g. a future anonymous type) until the release image breaks.
/// </para>
/// </remarks>
public static class DomainJson
{
    /// <summary>Serialize a state to UTF-8 bytes (for disk writes).</summary>
    public static byte[] SerializeToUtf8Bytes(State s) =>
        JsonSerializer.SerializeToUtf8Bytes(s, DomainJsonContext.Default.State);

    /// <summary>Serialize a state to string.</summary>
    public static string Serialize(State s) =>
        JsonSerializer.Serialize(s, DomainJsonContext.Default.State);

    /// <summary>Parse a state (for disk reads; lenient: ignores unknown fields).</summary>
    public static State? DeserializeState(string json) =>
        JsonSerializer.Deserialize(json, DomainJsonContext.Default.State);

    /// <summary>Serialize a conflict list to UTF-8 bytes (for disk writes).</summary>
    public static byte[] SerializeToUtf8Bytes(List<Conflict> conflicts) =>
        JsonSerializer.SerializeToUtf8Bytes(conflicts, DomainJsonContext.Default.ListConflict);

    /// <summary>Parse a conflict list (for disk reads).</summary>
    public static List<Conflict>? DeserializeConflicts(string json) =>
        JsonSerializer.Deserialize(json, DomainJsonContext.Default.ListConflict);

    /// <summary>Serialize one item to string (for tests and debugging).</summary>
    public static string SerializeItem(Item item) =>
        JsonSerializer.Serialize(item, DomainJsonContext.Default.Item);

    /// <summary>Parse a single item.</summary>
    public static Item DeserializeItem(string json) =>
        JsonSerializer.Deserialize(json, DomainJsonContext.Default.Item);
}
