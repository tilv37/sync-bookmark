using System.Text.Json;

using BookmarkSync.Domain;

namespace BookmarkSync.Server;

/// <summary>
/// healthcheck 子命令。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要它：最终镜像基于 FROM scratch，没有 shell 也没有 wget，
/// 所以 healthcheck 只能用 exec 形式调用二进制自身：
/// </para>
/// <code>
/// healthcheck: ["CMD", "/bmsync", "healthcheck"]
/// </code>
/// <para>
/// 它放在 Server 包而不是入口，是因为要读 BMSYNC_ADDR、
/// 并且要能被单元测试直接调用 —— 放在 Program 的 Main 里就只能靠起真进程来测，
/// 而这个逻辑恰恰值得测：测错了会表现为"容器明明活着却被反复重启"，
/// 或者反过来"挂了还显示 healthy"。
/// </para>
/// <para>
/// 退出码约定：
/// </para>
/// <list type="bullet">
/// <item>0 — 健康</item>
/// <item>1 — 服务不可达 / 状态异常 / 响应不是本服务</item>
/// <item>2 — 配置有问题（地址无法解析）—— 运维问题，跟服务健康无关</item>
/// </list>
/// <para>
/// 校验 body 里的 service 字段是为了防一种坑：反代配错端口，请求被路由到
/// 别的后端上，那个后端也返回 200。状态码过关但其实没人在服务。
/// </para>
/// </remarks>
public static class Healthcheck
{
    public const int ExitHealthy = 0;
    public const int ExitUnhealthy = 1;
    public const int ExitBadConfig = 2;

    public static int Run(IReadOnlyDictionary<string, string?>? env = null)
    {
        IReadOnlyDictionary<string, string?> e = env ?? ReadProcessEnv();

        string addr = ServerOptions.EnvOr(e, "BMSYNC_ADDR", ServerOptions.DefaultAddr);

        int colon = addr.LastIndexOf(':');
        if (colon < 0)
        {
            Console.Error.WriteLine("healthcheck: bad BMSYNC_ADDR: " + addr);
            return ExitBadConfig;
        }

        string host = addr[..colon];
        string port = addr[(colon + 1)..];

        // 容器里监听的是 0.0.0.0 / ::，但从容器内自请求要连回环地址。
        // 不做这个替换的话，容器启动后第一轮 healthcheck 必然失败，
        // 于是 K8s / compose 一直等，容器永远起不来。
        if (host.Length == 0 || host is "0.0.0.0" or "::" or "[::]")
        {
            host = "127.0.0.1";
        }

        if (host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('['))
        {
            host = "[" + host + "]"; // IPv6 字面量要加方括号
        }

        string url = $"http://{host}:{port}/api/health";

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            using HttpResponseMessage resp = client.GetAsync(url).GetAwaiter().GetResult();

            if (resp.StatusCode != System.Net.HttpStatusCode.OK)
            {
                Console.Error.WriteLine("healthcheck: status " + (int)resp.StatusCode);
                return ExitUnhealthy;
            }

            // 用 ApiJson.ParseHealth 而不是 JsonSerializer.Deserialize<HealthResponse>(s, options)：
            // 后者带 [RequiresDynamicCode]，PublishAot 下报 IL3050。
            //
            // 另外 HealthResponse 是 Server 的类型，用 BmsyncJson.Storage 会抛
            // "JsonTypeInfo metadata for type 'HealthResponse' was not provided"，
            // 而这个异常会被吞成 exit 1 —— 症状是"服务明明健康却被判为不健康"，
            // 指向完全错误的方向。
            var probe = ApiJson.ParseHealth(
                resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());

            if (probe is null || !probe.Ok || probe.Service != ServerOptions.ServiceName)
            {
                Console.Error.WriteLine("healthcheck: unexpected body");
                return ExitUnhealthy;
            }

            return ExitHealthy;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("healthcheck: " + ex.Message);
            return ExitUnhealthy;
        }
    }

    private static Dictionary<string, string?> ReadProcessEnv()
    {
        var d = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            d[(string)entry.Key] = (string?)entry.Value;
        }

        return d;
    }
}
