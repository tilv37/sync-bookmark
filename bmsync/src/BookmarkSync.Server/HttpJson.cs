using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using BookmarkSync.Domain;

namespace BookmarkSync.Server;

/// <summary>Hard limits for the HTTP layer.</summary>
/// <remarks>
/// Domain limits (MaxItems / MaxDepth / KeyLen ...) live in BookmarkSync.Domain/Limits.cs.
/// They are separate because they govern different things: here "how big/fast may an
/// HTTP request be", there "how complex may bookmark data be". Merging them would hide
/// whether raising a number affects the network or the data shape.
/// </remarks>
public static class HttpLimits
{
    /// <summary>/api/sync request body cap.</summary>
    /// <remarks>
    /// Note: this is only the app-level cap. Nginx enforces another <b>smaller</b> limit
    /// (client_max_body_size, default 1m) that users must raise in NPM;
    /// see docs/architecture.md §9. Past ~6000 bookmarks the nginx cap hits first.
    /// </remarks>
    public const long MaxBodyBytes = 16L * 1024 * 1024;

    /// <summary>
    /// Per-source sync request rate limit.
    /// </summary>
    /// <remarks>
    /// Why 60 and not a single digit: this is a <b>manually triggered</b> tool whose normal
    /// use is "click sync once". A typical session includes: first sync, first pull on the
    /// work machine, a few catch-up syncs, retries after failures, plus a conflict-list fetch.
    /// At 10/min those legitimate calls would all 429 after the first few, forcing users to
    /// wait a minute for doing nothing — blocking no real attacker while hurting normal use.
    /// <para>
    /// The real target is "block scripted high-frequency calls", and 60/min (1 per second)
    /// is far above human click speed. Volume is small too: 60 x 800KB uploads
    /// strain no VPS.
    /// </para>
    /// </remarks>
    public const int RateLimitPerMinute = 60;
}

/// <summary>JSON encode/decode and body-read helpers.</summary>
public static class HttpJson
{
    /// <summary>
    /// Serialization options: same set as <see cref="BmsyncJson.Storage"/> (ordered keys,
    /// omitted empty fields, unescaped CJK), but unknown fields are <b>not</b> rejected —
    /// responses are shapes we emit ourselves, so "unknown fields" cannot occur.
    /// </summary>
    // Actually defined in ApiJsonContext.cs — see why ItemJsonConverter / StateJsonConverter
    // must be added back explicitly (source generation would bypass them).
    public static readonly JsonSerializerOptions Response = ApiJson.Response;

    /// <summary>Write a JSON response.</summary>
    /// <remarks>
    /// Content-Type is set explicitly instead of relying on the framework ObjectResult: the
    /// framework writes <c>application/json; charset=utf-8</c>, but if an endpoint ever returns
    /// a pre-serialized string the charset silently disappears, while the extension reads via
    /// <c>response.json()</c> — which then fails with a misleading "Unexpected token".
    /// error.
    /// </remarks>
    public static async Task WriteJsonAsync<T>(HttpContext ctx, int status, T value, JsonTypeInfo<T> info)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";

        // Use the JsonTypeInfo overload, not JsonSerializer.SerializeAsync(stream, v, options):
        // the latter carries [RequiresDynamicCode] and fails PublishAot with IL3050, same as
        // the ApiJson case.
        await JsonSerializer.SerializeAsync(ctx.Response.Body, value, info, ctx.RequestAborted)
            .ConfigureAwait(false);
    }

    public static Task WriteErrorAsync(HttpContext ctx, int status, string code, string message) =>
        WriteJsonAsync(
            ctx, status,
            new ErrorResponse { Code = code, Message = message },
            ApiJson.TypeInfoOf<ErrorResponse>());

    /// <summary>
    /// Read the request body with a size cap.
    /// </summary>
    /// <returns>
    /// Returns true with bytes in <paramref name="buffer"/> on success;
    /// returns false (with <paramref name="tooLarge"/> true) when over the cap.
    /// </returns>
    /// <remarks>
    /// Read manually instead of model binding: to control exactly when 413 fires and what the
    /// error body is, and to know the size <b>before parsing starts</b> — otherwise a 100MB bad
    /// upload would be buffered into memory first and rejected only after costing 100MB.
    /// </remarks>
    public static async Task<(bool Ok, bool TooLarge, byte[]? Body)> ReadBodyAsync(
        HttpContext ctx, long limit)
    {
        var buffer = new MemoryStream(capacity: 8192);
        byte[] chunk = ArrayPool<byte>.Shared.Rent(81920);

        try
        {
            int read;
            while ((read = await ctx.Request.Body.ReadAsync(chunk, ctx.RequestAborted).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > limit)
                {
                    return (false, true, null);
                }

                buffer.Write(chunk, 0, read);
            }

            return (true, false, buffer.ToArray());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }
}
