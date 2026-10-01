using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace BookmarkSync.Server;

/// <summary>
/// Auth and rate limiting. The only trust boundary in this project.
/// </summary>
/// <remarks>
/// Every line changed here should be a deliberate decision, never drive-by refactoring.
/// </remarks>
public static class Auth
{
    /// <summary>
    /// Extract the token from the Authorization header. The Bearer prefix is
    /// case-insensitive (RFC 7235 schemes are case-insensitive).
    /// </summary>
    public static string BearerToken(HttpRequest request)
    {
        string header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";

        return header.Length > prefix.Length
               && header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? header[prefix.Length..]
            : string.Empty;
    }

    /// <summary>
    /// Compare two tokens in constant time.
    /// </summary>
    /// <remarks>
    /// Guards against guessing the token byte-by-byte via response-time differences. Length
    /// is compared first: <c>CryptographicOperations.FixedTimeEquals</c> returns **immediately**
    /// on unequal lengths, which distinguishes "right length, wrong content" from "wrong
    /// length" by timing — but token length is public (fixed in config), so that leak is
    /// harmless; what must be protected is the equal-length case.
    /// </remarks>
    public static bool TokenMatches(string presented, string expected)
    {
        byte[] a = Encoding.UTF8.GetBytes(presented);
        byte[] b = Encoding.UTF8.GetBytes(expected);

        if (a.Length != b.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>
    /// Resolve the real client IP. Behind a reverse proxy the first X-Forwarded-For segment
    /// is the true source; only the first segment is used so a forged header cannot bypass limits.
    /// </summary>
    /// <remarks>
    /// ⚠️ This trusts that the front reverse proxy correctly sets and overwrites X-Forwarded-For.
    /// If the service were exposed directly to the internet, an attacker could forge this
    /// header to bypass rate limiting. Binding 127.0.0.1 in compose is exactly for this — see docs/architecture.md §9.
    /// </remarks>
    public static string ClientIp(HttpRequest request)
    {
        string xff = request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrEmpty(xff))
        {
            int comma = xff.IndexOf(',', StringComparison.Ordinal);
            return comma >= 0
                ? xff[..comma].Trim()
                : xff.Trim();
        }

        IPAddress? remote = request.HttpContext.Connection.RemoteIpAddress;
        return remote is null
            ? "unknown"
            : IPAddress.IsLoopback(remote) ? remote.ToString() : remote.MapToIPv4().ToString();
    }

    /// <summary>
    /// Shared middleware for all data endpoints: rate limiting + Bearer token auth.
    /// </summary>
    /// <remarks>
    /// Order matters: rate limit before auth. Otherwise someone with a wrong token could use
    /// the 429/401 difference to tell whether a token exists.
    /// <para>
    /// Returns <see cref="Func{RequestDelegate, RequestDelegate}"/> rather than
    /// <see cref="RequestDelegate"/>: ASP.NET Core middleware is structured "next outside,
    /// self inside", so only the factory form hands control downstream correctly.
    /// A single-parameter delegate without next fails to compile at best; calling an empty
    /// next would make every protected endpoint <b>never run</b> with only an "empty
    /// response" symptom — the hardest bug class to trace.
    /// </para>
    /// </remarks>
    public static Func<RequestDelegate, RequestDelegate> Protect(
        ServerOptions cfg, RateLimiter rl, ILogger log)
    {
        return next => async ctx =>
        {
            string ip = ClientIp(ctx.Request);

            if (!rl.Allow(ip))
            {
                ctx.Response.Headers["Retry-After"] = "60";
                await HttpJson
                    .WriteErrorAsync(ctx, StatusCodes.Status429TooManyRequests,
                        ErrorCodes.RateLimited, "Too many requests, please try again later")
                    .ConfigureAwait(false);
                return;
            }

            string presented = BearerToken(ctx.Request);
            if (!TokenMatches(presented, cfg.Token))
            {
                log.LogWarning("Auth failed {Ip} {Path}", ip, ctx.Request.Path.Value);
                await HttpJson
                    .WriteErrorAsync(ctx, StatusCodes.Status401Unauthorized,
                        ErrorCodes.Unauthorized, "Invalid access token")
                    .ConfigureAwait(false);
                return;
            }

            await next(ctx).ConfigureAwait(false);
        };
    }
}
