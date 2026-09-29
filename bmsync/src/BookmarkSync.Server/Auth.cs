using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace BookmarkSync.Server;

/// <summary>
/// 鉴权与限流。这是本项目唯一的"信任边界"。
/// </summary>
/// <remarks>
/// 改动这里的每一行都应该是一次有意识的决定，而不是顺手重构。
/// </remarks>
public static class Auth
{
    /// <summary>
    /// 从 Authorization 头里取出令牌。Bearer 前缀大小写不敏感
    /// （RFC 7235 的 scheme 是 case-insensitive）。
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
    /// 恒定时间比较两个令牌。
    /// </summary>
    /// <remarks>
    /// 防的是"通过响应时间差异逐字节猜出 token"。但要先比长度：
    /// <c>CryptographicOperations.FixedTimeEquals</c> 在长度不等时**立即**
    /// 返回 false，直接用会让"长度对但内容错"和"长度就不对"在耗时上可区分 ——
    /// 而 token 长度是公开的（写死在配置里），所以这个区分无害，
    /// 真正要保护的是长度相同的情况。
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
    /// 取真实客户端 IP。反代场景下 X-Forwarded-For 的第一段才是真实来源；
    /// 只取第一段，避免伪造的头绕过限流。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这信任前提是「前置反代会正确设置并覆盖 X-Forwarded-For」。
    /// 若服务可能直接暴露在公网，攻击者能随便伪造这个头来绕过限流。
    /// compose 里绑定 127.0.0.1 正是为此 —— 见 docs/design.md §10.3。
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
    /// 所有数据接口共用的中间件：限流 + Bearer token 鉴权。
    /// </summary>
    /// <remarks>
    /// 顺序很重要：先限流再鉴权。否则一个拿到错误 token 的人可以用
    /// 429/401 的响应差异来区分"token 是否存在"。
    /// <para>
    /// 返回 <see cref="Func{RequestDelegate, RequestDelegate}"/> 而不是
    /// <see cref="RequestDelegate"/>：ASP.NET Core 的中间件是
    /// "next 在外、自己在里" 的结构，工厂形式才能正确地把控制权交给下游。
    /// 写成不接受 next 的单参数委托，最多是编译不过；写成"调用一个空的
    /// next"，则会让所有受保护的端点变成<b>永远不执行</b>，而症状只是
    /// "接口返回空响应" —— 那是最难查的一类 bug。
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
                        ErrorCodes.RateLimited, "请求过于频繁，请稍后再试")
                    .ConfigureAwait(false);
                return;
            }

            string presented = BearerToken(ctx.Request);
            if (!TokenMatches(presented, cfg.Token))
            {
                log.LogWarning("鉴权失败 {Ip} {Path}", ip, ctx.Request.Path.Value);
                await HttpJson
                    .WriteErrorAsync(ctx, StatusCodes.Status401Unauthorized,
                        ErrorCodes.Unauthorized, "访问令牌无效")
                    .ConfigureAwait(false);
                return;
            }

            await next(ctx).ConfigureAwait(false);
        };
    }
}
