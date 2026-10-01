using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using BookmarkSync.Domain;
using BookmarkSync.Store;

namespace BookmarkSync.Server;

/// <summary>
/// Source-generated JSON context for API types.
/// </summary>
/// <remarks>
/// Same as <see cref="DomainJsonContext"/>: <c>PublishAot=true</c> disables reflection-based
/// JSON, so every serialized type must be registered here.
/// <para>
/// Domain types (State / Item / Conflict / Summary) must be registered <b>again</b> here:
/// generated contexts are independent; the Server context does not see Domain's.
/// Only the "type list" is duplicated, not the implementation.
/// </para>
/// </remarks>
// Encoder must explicitly be UnsafeRelaxedJsonEscaping.
//
// Leaving it out does not work — the generator bakes the encoder into the type info,
// whose default is JavaScriptEncoder.Default: CJK, &, < all become \uXXXX.
// The result is "compiles, most tests pass, API returns 200", with only the assertion
// "response keeps CJK unescaped" failing — the one case most easily misread as "the
// test is wrong" and edited away.
//
// Why this pitfall deserves its own note: ApiJson.Response (runtime-built options)
// sets the same Encoder, and both places need it. Setting only one gives
// "state.json on disk is right but CJK in API responses is escaped" —
// two escaping rules in one program, harder to trace than uniform escaping.
// This attribute **cannot** set Encoder — JsonSourceGenerationOptionsAttribute
// has no Encoder property (verified by reflection: only WriteIndented / NumberHandling /
// Converters / UnmappedMemberHandling and 17 more, no Encoder).
//
// Without Encoder the context's own options use the default encoder,
// so CJK, &, < all become \uXXXX. Measured behavior:
//
//   Ctx.Default.Dto  →  {"N":"\u4E2D\u6587 \u0026 \u003Ctag>"}   ← escaped
//   opt+TypeInfo     →  {"N":"<CJK> & <tag>"}                    ← raw
//
// In short: **the context's own type info drops the escaping setting,
// so type info must come from runtime-built options.** See TypeInfoOf below.
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
/// Actual JSON settings used by the API layer.
/// </summary>
/// <remarks>
/// <para>
/// Do not use <see cref="ApiJsonContext"/>'s <c>Default.Options</c> directly: source
/// generation emits "standard" metadata for <see cref="State"/> that <b>bypasses</b> the
/// <see cref="ItemJsonConverter"/> / <see cref="StateJsonConverter"/> in <c>Domain</c>.
/// Those two converters are what guarantee key sorting and empty-field omission — without
/// them, state.json keys fall back to dictionary insertion order and empty fields are emitted.
/// </para>
/// <para>
/// Both converters are added back to <c>Converters</c> explicitly:
/// <c>JsonSerializerOptions.Converters</c> outranks generated metadata.
/// </para>
/// <para>
/// <b>This pitfall is worth its own note</b>: its symptom is "state.json order scrambled,
/// size grown" rather than any error — compiles, tests pass, API still returns 200.
/// Only a byte-for-byte output comparison catches it.
/// </para>
/// </remarks>
public static class ApiJson
{
    /// <summary>For writing responses.</summary>
    public static readonly JsonSerializerOptions Response = Create(
        JsonUnmappedMemberHandling.Skip);

    /// <summary>For parsing client requests: unknown fields are errors.</summary>
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

    // ── AOT-safe read/write entry points ──────────────────────────────
    //
    // Two constraints meet here:
    //
    // 1. Do not use the JsonSerializer.Serialize(value, Response) generic overload —
    //    it carries [RequiresDynamicCode] and fails PublishAot with IL3050.
    //    The JsonTypeInfo<T> overload sidesteps it (a false positive here: types are fixed
    //    and fields are written by hand-written converters, so no runtime codegen is needed).
    //
    // 2. Do not use ApiJsonContext.Default.XXX (the context's own type info) —
    //    see the measurement at the top of this file: it carries the default encoder and escapes CJK.
    //
    // So everything goes through TypeInfoOf<T>(): type info from the **runtime** options,
    // which has both the TypeInfoResolver (generated metadata) and the Encoder (no escaping).
    //
    // Do not suppress IL3050 with NoWarn — that would also swallow genuine AOT
    // incompatibilities (e.g. a future anonymous type) until the release image breaks.

    /// <summary>
    /// Take type info from the runtime options. Do **not** switch to
    /// <c>ApiJsonContext.Default.XXX</c>; see the measurement note at the top of this file.
    /// </summary>
    public static JsonTypeInfo<T> TypeInfoOf<T>() => (JsonTypeInfo<T>)Response.GetTypeInfo(typeof(T));

    /// <summary>
    /// Take type info from the <b>strict</b> options. Client-request parsing must use this.
    /// </summary>
    /// <remarks>
    /// Two entry points is not duplication: <see cref="TypeInfoOf{T}"/> uses <c>Response</c>
    /// (unknown fields skipped; state.json is a persisted format where an old build reading a
    /// newer file must not crash), while the request side requires
    /// <see cref="JsonUnmappedMemberHandling.Disallow"/> — a client misspelling <c>device</c> as
    /// <c>devcie</c> must surface immediately rather than being silently treated as a default device.
    /// <para>
    /// Verified by test: the two behave differently on <c>{"devcie":"typo"}</c>
    /// (Skip passes silently / Disallow throws JsonException), so they must stay separate.
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
