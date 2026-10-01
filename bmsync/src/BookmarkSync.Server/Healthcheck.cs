using System.Text.Json;

using BookmarkSync.Domain;

namespace BookmarkSync.Server;

/// <summary>
/// healthcheck subcommand.
/// </summary>
/// <remarks>
/// <para>
/// Why it exists: the final image is FROM scratch with no shell and no wget,
/// so healthcheck can only exec the binary itself:
/// </para>
/// <code>
/// healthcheck: ["CMD", "/bmsync", "healthcheck"]
/// </code>
/// <para>
/// It lives in the Server package (not the entry point) because it reads BMSYNC_ADDR
/// and must be unit-testable — inside Program.Main only real-process tests could cover it,
/// yet this logic deserves tests: a wrong verdict shows as "a live container keeps restarting",
/// or the reverse, "dead but still healthy".
/// </para>
/// <para>
/// Exit code contract:
/// </para>
/// <list type="bullet">
/// <item>0 — healthy</item>
/// <item>1 — unreachable / bad state / response is not this service</item>
/// <item>2 — bad config (address unparseable) — an ops problem, unrelated to service health</item>
/// </list>
/// <para>
/// Checking the body service field guards against one pitfall: a reverse proxy with the
/// wrong port routes the request to another backend that also returns 200.
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

        // Inside a container the listener is 0.0.0.0 / ::, but self-requests must use loopback.
        // Without this rewrite the first healthcheck after container start always fails,
        // so K8s / compose waits forever and the container never comes up.
        if (host.Length == 0 || host is "0.0.0.0" or "::" or "[::]")
        {
            host = "127.0.0.1";
        }

        if (host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('['))
        {
            host = "[" + host + "]"; // IPv6 literals need brackets
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

            // Use ApiJson.ParseHealth, not JsonSerializer.Deserialize<HealthResponse>(s, options):
            // the latter carries [RequiresDynamicCode] and fails PublishAot with IL3050.
            //
            // HealthResponse is also a Server type, so BmsyncJson.Storage throws
            // "JsonTypeInfo metadata for type 'HealthResponse' was not provided",
            // and that exception collapses into exit 1 — a healthy service judged unhealthy,
            // pointing in a completely wrong direction.
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
