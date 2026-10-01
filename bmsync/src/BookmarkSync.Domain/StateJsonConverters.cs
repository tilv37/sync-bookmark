using System.Text.Json;
using System.Text.Json.Serialization;

namespace BookmarkSync.Domain;

/// <summary>
/// JSON converter for <see cref="Item"/>, aligned byte-for-byte with Go's <c>encoding/json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written instead of relying on <c>[JsonIgnore(Condition = WhenWritingDefault)]</c>
/// because of a verified pitfall:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Empty strings are not omitted.</b> C# <c>WhenWritingDefault</c> treats only
/// <c>null</c> as default for <c>string</c>, so <c>""</c> is still written as
/// <c>"u":""</c>; Go's <c>omitempty</c> omits <c>== ""</c>. Confirmed with a
/// minimal repro, not from memory.
/// </item>
/// <item>
/// <b>Non-ASCII escaping.</b> .NET escapes CJK characters as <c>\uXXXX</c> (6x size)
/// while Go emits raw UTF-8. Bookmark titles are largely CJK, so 5000 bookmarks
/// would grow from ~800KB to 4MB+ and hit nginx <c>client_max_body_size</c>. The fix
/// is a global <see cref="JsonSerializerOptions.Encoder"/> of
/// <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> (see <see cref="BmsyncJson"/>).
/// </item>
/// </list>
/// <para>
/// Fields are written in Go struct declaration order: p t n u a m d x. Not for
/// compatibility (JSON parsers ignore order), but to keep state.json diffs readable
/// and byte-comparable with the Go build.
/// </para>
/// </remarks>
public sealed class ItemJsonConverter : JsonConverter<Item>
{
    public override Item Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"item must be a JSON object, got {reader.TokenType}");
        }

        string p = string.Empty, t = string.Empty, n = string.Empty, u = string.Empty;
        string a = string.Empty, m = string.Empty;
        bool d = false;
        long x = 0;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return new Item { P = p, T = t, N = n, U = u, A = a, M = m, D = d, X = x };
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException($"unexpected {reader.TokenType} inside item");
            }

            string prop = reader.GetString()!;
            if (!reader.Read())
            {
                throw new JsonException($"missing value after item field {prop}");
            }

            switch (prop)
            {
                // Missing fields and null both become empty strings, matching Go leaving
                // absent fields at the "" zero value. Null is tolerated because a client
                // omitting a field should not doom the whole upload — hard errors are
                // reserved for malformed data (see State.Validate), not missing fields.
                case "p": p = ReadStringOrEmpty(ref reader); break;
                case "t": t = ReadStringOrEmpty(ref reader); break;
                case "n": n = ReadStringOrEmpty(ref reader); break;
                case "u": u = ReadStringOrEmpty(ref reader); break;
                case "a": a = ReadStringOrEmpty(ref reader); break;
                case "m": m = ReadStringOrEmpty(ref reader); break;
                case "d": d = reader.TokenType == JsonTokenType.True; break;
                case "x":
                    x = reader.TokenType == JsonTokenType.Number ? reader.GetInt64() : 0;
                    break;
                default:
                    // Unknown fields depend on context: parsing a client request must fail
                    // (a typoed field name has to surface immediately), while reading our
                    // own state.json must pass (it is a persisted format; an old build
                    // reading a newer file must not crash on added fields).
                    //
                    // UnmappedMemberHandling only applies to reflection-based
                    // deserialization, so a custom converter must read it itself. Missing
                    // it makes strict mode toothless — and untestable, since requests
                    // still succeed while misspelled fields are silently dropped.
                    if (options.UnmappedMemberHandling == JsonUnmappedMemberHandling.Disallow)
                    {
                        throw new JsonException($"item contains unknown field \"{prop}\"");
                    }

                    reader.Skip();
                    break;
            }
        }

        throw new JsonException("unterminated item object");
    }

    public override void Write(Utf8JsonWriter writer, Item value, JsonSerializerOptions options) =>
        WriteItem(writer, value, options);

    /// <summary>
    /// Actual item writer. Internal so <see cref="StateJsonConverter"/> can call it
    /// directly and avoid the <c>JsonSerializer.Serialize</c> generic overload's AOT
    /// warnings (IL2026/IL3050).
    /// </summary>
    internal static void WriteItem(Utf8JsonWriter writer, Item value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        WriteString(writer, "p", value.P);
        WriteString(writer, "t", value.T);
        WriteString(writer, "n", value.N);
        WriteString(writer, "u", value.U);
        WriteString(writer, "a", value.A);
        WriteString(writer, "m", value.M);
        if (value.D)
        {
            writer.WriteBoolean("d", true);
        }

        if (value.X != 0)
        {
            writer.WriteNumber("x", value.X);
        }

        writer.WriteEndObject();
    }

    private static string ReadStringOrEmpty(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return string.Empty;
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"expected string, got {reader.TokenType}");
        }

        return reader.GetString() ?? string.Empty;
    }

    private static void WriteString(Utf8JsonWriter writer, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            writer.WriteString(name, value);
        }
    }
}

/// <summary>
/// JSON converter for <see cref="State"/>: writes items with keys <b>sorted</b>.
/// </summary>
/// <remarks>
/// Go's <c>encoding/json</c> emits maps with sorted keys; .NET's <c>Dictionary</c>
/// iterates in insertion order. The mismatch would not break parsing — it would make
/// key order in state.json random, so every sync rewrite produces a noisy
/// <c>git diff</c> and the two implementations could not be compared byte-for-byte.
/// </remarks>
public sealed class StateJsonConverter : JsonConverter<State>
{
    public override State Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"state must be a JSON object, got {reader.TokenType}");
        }

        int v = 0;
        string hlc = string.Empty;
        Dictionary<string, Item>? items = null;

        var itemConverter = new ItemJsonConverter();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return new State
                {
                    V = v,
                    Clock = hlc,
                    Items = items ?? State.NewItemsDictionary(),
                };
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException($"unexpected {reader.TokenType} inside state");
            }

            string prop = reader.GetString()!;
            if (!reader.Read())
            {
                throw new JsonException($"missing value after state field {prop}");
            }

            switch (prop)
            {
                case "v":
                    v = reader.TokenType == JsonTokenType.Number ? reader.GetInt32() : 0;
                    break;

                case "hlc":
                    hlc = reader.TokenType == JsonTokenType.String ? reader.GetString() ?? string.Empty : string.Empty;
                    break;

                case "items":
                    if (reader.TokenType == JsonTokenType.Null)
                    {
                        items = State.NewItemsDictionary();
                        break;
                    }

                    items = ReadItems(ref reader, itemConverter, options);
                    break;

                default:
                    if (options.UnmappedMemberHandling == JsonUnmappedMemberHandling.Disallow)
                    {
                        throw new JsonException($"state contains unknown field \"{prop}\"");
                    }

                    reader.Skip();
                    break;
            }
        }

        throw new JsonException("unterminated state object");
    }

    public override void Write(Utf8JsonWriter writer, State value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("v", value.V);
        if (!string.IsNullOrEmpty(value.Clock))
        {
            writer.WriteString("hlc", value.Clock);
        }

        writer.WritePropertyName("items");
        writer.WriteStartObject();

        foreach (string k in value.KeysSorted())
        {
            writer.WritePropertyName(k);
            WriteItem(writer, value.Items[k], options);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    /// <summary>
    /// Write one item by calling <see cref="ItemJsonConverter"/> directly instead of
    /// <c>JsonSerializer.Serialize(writer, item, options)</c>.
    /// </summary>
    /// <remarks>
    /// The generic overload carries <c>[RequiresUnreferencedCode]</c> and
    /// <c>[RequiresDynamicCode]</c>, which fail PublishAot with IL2026 / IL3050 about
    /// "JSON serialization needs runtime codegen" — but nothing here needs runtime
    /// codegen: the converter is hand-written and field names are hard-coded.
    /// <para>
    /// The error is a <b>false positive</b>: the generic overload assumes the worst
    /// case, while we know exactly which converter to use. Calling it explicitly is
    /// the right fix; adding <c>NoWarn</c> would also swallow genuine AOT issues.
    /// </para>
    /// </remarks>
    private static void WriteItem(Utf8JsonWriter writer, Item item, JsonSerializerOptions options) =>
        ItemJsonConverter.WriteItem(writer, item, options);

    private static Dictionary<string, Item> ReadItems(
        ref Utf8JsonReader reader,
        ItemJsonConverter itemConverter,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"items must be a JSON object, got {reader.TokenType}");
        }

        var items = State.NewItemsDictionary();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return items;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException($"unexpected {reader.TokenType} inside items");
            }

            string key = reader.GetString()!;
            if (!reader.Read())
            {
                throw new JsonException($"missing value after item {key}");
            }

            items[key] = itemConverter.Read(ref reader, typeof(Item), options);
        }

        throw new JsonException("unterminated items object");
    }
}
