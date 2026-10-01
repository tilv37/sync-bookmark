using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BookmarkSync.Domain;

/// <summary>
/// Project-wide JSON settings.
/// </summary>
/// <remarks>
/// <para>
/// Each of the three settings carries its "why": every one changes byte output
/// vs the legacy Go implementation, and "why it is written this way" is easier
/// to lose in later refactors than "what is written":
/// </para>
/// <list type="number">
/// <item>
/// <c>UnsafeRelaxedJsonEscaping</c>: .NET escapes CJK as <c>\uXXXX</c> by
/// default while Go emits raw UTF-8. Bookmark titles are largely CJK; defaults
/// would grow a 5000-bookmark body from ~800 KB to 4 MB+, hitting nginx
/// <c>client_max_body_size</c>.
/// <para>
/// The "Unsafe" encoder is safe here: this service only serves a JSON API,
/// responses are never inlined into an HTML page, so there is no HTML-injection
/// scenario. If inlining were ever needed, the fix would be "don't inline",
/// not "escape all CJK".
/// </para>
/// </item>
/// <item>
/// <c>Item</c> / <c>State</c> use custom converters: see
/// <see cref="ItemJsonConverter"/> and <see cref="StateJsonConverter"/>.
/// </item>
/// <item>
/// <c>UnmappedMemberHandling = Disallow</c>: a client with a misspelled field
/// should get an immediate clear error instead of silently ignoring it and
/// behaving oddly (e.g. token spelled "tokne" yielding endless 401s).
/// <para>
/// <b>Request parsing only.</b> Files we wrote ourselves (state.json) must
/// allow unknown fields — as a persisted format, an old build reading a newer
/// file must not crash when fields are added.
/// </para>
/// </item>
/// </list>
/// </remarks>
public static class BmsyncJson
{
    /// <summary>For state.json / conflict logs: relaxed, ordered keys, omit empty fields.</summary>
    public static readonly JsonSerializerOptions Storage = Create(disableUnknownFields: false);

    /// <summary>For client requests: unknown fields are errors.</summary>
    public static readonly JsonSerializerOptions StrictRequest = Create(disableUnknownFields: true);

    private static JsonSerializerOptions Create(bool disableUnknownFields) => new()
    {
        // Source-generated context: PublishAot disables reflection JSON, see DomainJsonContext.
        TypeInfoResolver = DomainJsonContext.Default,

        // See item 1 above.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,

        // See item 2 above.
        Converters =
        {
            new ItemJsonConverter(),
            new StateJsonConverter(),
        },

        // See item 3 above.
        UnmappedMemberHandling = disableUnknownFields
            ? JsonUnmappedMemberHandling.Disallow
            : JsonUnmappedMemberHandling.Skip,

        // No indentation: state.json is a full-sync payload; indentation bloats it 3-5x.
        WriteIndented = false,

        // Strict numbers. Bookmark data has no floats, but strictness beats
        // "some day a double sneaks in and precision silently changes".
        NumberHandling = JsonNumberHandling.Strict,
    };
}
