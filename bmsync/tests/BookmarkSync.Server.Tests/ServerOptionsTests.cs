using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using BookmarkSync.Server;

namespace BookmarkSync.Server.Tests;

/// <summary>
/// Config-parsing tests, mirroring the config-parsing test cases.
/// </summary>
/// <remarks>
/// All settings come from env vars for containerized deploys. Tests pass dictionaries instead of
/// mutating <see cref="Environment"/>: real env vars are process-wide and parallel tests would
/// pollute each other — "which case polluted which" is a particularly nasty flake.
/// </remarks>
public class ServerOptionsTests
{
    /// <summary>Build an env-var dictionary; null values mean "unset".</summary>
    private static Dictionary<string, string?> Env(params (string Key, string? Value)[] kv)
    {
        var d = new Dictionary<string, string?>(StringComparer.Ordinal) { ["BMSYNC_TOKEN"] = new string('t', 40) };
        foreach ((string k, string? v) in kv)
        {
            d[k] = v;
        }

        return d;
    }

    [Fact]
    public void DefaultValues()
    {
        ServerOptions cfg = ServerOptions.Load(Env());

        Assert.Equal(":8080", cfg.Addr);
        Assert.Equal("/data", cfg.DataDir);
        Assert.Equal(30, cfg.HistoryKeep);
        Assert.Equal(TimeSpan.FromDays(90), cfg.TombstoneTtl);
    }

    [Fact]
    public void EnvVarsOverride()
    {
        ServerOptions cfg = ServerOptions.Load(Env(
            ("BMSYNC_ADDR", "127.0.0.1:9999"),
            ("BMSYNC_DATA", "/srv/bmsync"),
            ("BMSYNC_HISTORY_KEEP", "7"),
            ("BMSYNC_TOMBSTONE_TTL_DAYS", "30")));

        Assert.Equal("127.0.0.1:9999", cfg.Addr);
        Assert.Equal("/srv/bmsync", cfg.DataDir);
        Assert.Equal(7, cfg.HistoryKeep);
        Assert.Equal(TimeSpan.FromDays(30), cfg.TombstoneTtl);
    }

    /// <summary>
    /// A mistyped config falls back to defaults with a warning instead of refusing to start.
    /// </summary>
    /// <remarks>
    /// Rationale in the <see cref="ServerOptions.EnvIntOr"/> note: a slip like
    /// <c>HISTORY_KEEP=30days</c> would look like a broken service when defaults would sync fine.
    /// </remarks>
    [Fact]
    public void InvalidNumbersFallBackToDefaults()
    {
        ServerOptions cfg = ServerOptions.Load(Env(
            ("BMSYNC_HISTORY_KEEP", "lots"),
            ("BMSYNC_TOMBSTONE_TTL_DAYS", "abc")));

        Assert.Equal(30, cfg.HistoryKeep);
        Assert.Equal(TimeSpan.FromDays(90), cfg.TombstoneTtl);
    }

    public static TheoryData<string, Dictionary<string, string?>, string> RejectionCases()
    {
        var data = new TheoryData<string, Dictionary<string, string?>, string>();
        var token = new string('x', 40);

        data.Add(
            "token unset",
            new Dictionary<string, string?> { ["BMSYNC_TOKEN"] = null },
            "BMSYNC_TOKEN");

        data.Add(
            "token too short",
            new Dictionary<string, string?> { ["BMSYNC_TOKEN"] = "short" },
            "BMSYNC_TOKEN");

        data.Add(
            "token exactly 31 chars",
            new Dictionary<string, string?> { ["BMSYNC_TOKEN"] = new string('x', 31) },
            "BMSYNC_TOKEN");

        data.Add(
            "zero snapshots kept",
            new Dictionary<string, string?> { ["BMSYNC_TOKEN"] = token, ["BMSYNC_HISTORY_KEEP"] = "0" },
            "HISTORY_KEEP");

        data.Add(
            "tombstone TTL under a day",
            new Dictionary<string, string?> { ["BMSYNC_TOKEN"] = token, ["BMSYNC_TOMBSTONE_TTL_DAYS"] = "0" },
            "TOMBSTONE");

        return data;
    }

    [Theory]
    [MemberData(nameof(RejectionCases))]
    public void RejectsInvalidConfig(string name, Dictionary<string, string?> env, string wantSub)
    {
        InvalidOperationException ex =
            Assert.Throws<InvalidOperationException>(() => ServerOptions.Load(env));

        Assert.False(string.IsNullOrEmpty(name)); // Case names aid triage, hence kept as parameters

        Assert.Contains(wantSub, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A token of exactly 32 chars is the minimum and must pass.</summary>
    [Fact]
    public void Accepts32CharToken()
    {
        var cfg = ServerOptions.Load(new Dictionary<string, string?>
        {
            ["BMSYNC_TOKEN"] = new string('x', ServerOptions.MinTokenLen),
            ["BMSYNC_HISTORY_KEEP"] = "30",
            ["BMSYNC_TOMBSTONE_TTL_DAYS"] = "90",
        });

        Assert.Equal(32, cfg.Token.Length);
    }

    [Fact]
    public void EnvOr_UsesDefaultWhenUnset()
    {
        Assert.Equal("fallback", ServerOptions.EnvOr(new Dictionary<string, string?>(), "X", "fallback"));
        Assert.Equal("fallback", ServerOptions.EnvOr(new Dictionary<string, string?> { ["X"] = "" }, "X", "fallback"));
        Assert.Equal("explicit", ServerOptions.EnvOr(new Dictionary<string, string?> { ["X"] = "explicit" }, "X", "fallback"));
    }
}

/// <summary>
/// Rate-limiter tests, mirroring the rate-limiter test cases.
/// </summary>
public class RateLimiterTests
{
    [Fact]
    public void OverLimitWithinWindowIsRejected()
    {
        RateLimiter rl = new(2, TimeSpan.FromMinutes(1));

        Assert.True(rl.Allow("1.1.1.1"));
        Assert.True(rl.Allow("1.1.1.1"));
        Assert.False(rl.Allow("1.1.1.1"));
    }

    [Fact]
    public void DifferentSourcesDoNotInterfere()
    {
        RateLimiter rl = new(1, TimeSpan.FromMinutes(1));

        Assert.True(rl.Allow("1.1.1.1"));
        Assert.False(rl.Allow("1.1.1.1"));
        Assert.True(rl.Allow("2.2.2.2"));
    }

    [Fact]
    public void ReadmittedAfterWindowExpires()
    {
        RateLimiter rl = new(2, TimeSpan.FromMinutes(1));

        rl.Allow("1.1.1.1");
        rl.Allow("1.1.1.1");
        Assert.False(rl.Allow("1.1.1.1"));

        // Push the bucket start into the past, simulating a slid window
        rl.SeedBucket("1.1.1.1", 2, DateTimeOffset.UtcNow.AddHours(-1));
        Assert.True(rl.Allow("1.1.1.1"));
    }

    [Fact]
    public void GcReapsExpiredBuckets()
    {
        RateLimiter rl = new(5, TimeSpan.FromMinutes(1));
        Assert.True(rl.Allow("1.1.1.1"));

        // Push both the bucket and lastGC into the past so the next Allow triggers cleanup
        rl.SeedBucket("1.1.1.1", 1, DateTimeOffset.UtcNow.AddHours(-1));
        rl.ForceGcDue();

        rl.Allow("2.2.2.2");
        rl.Allow("3.3.3.3");

        Assert.Equal(2, rl.BucketCount);
    }

    [Fact]
    public void NoCleanupBeforeWindowPasses()
    {
        RateLimiter rl = new(5, TimeSpan.FromHours(1));
        rl.Allow("1.1.1.1");

        rl.Allow("2.2.2.2");

        // The window is an hour and lastGc was just initialized — no cleanup expected
        Assert.Equal(2, rl.BucketCount);
    }
}

/// <summary>
/// healthcheck subcommand tests, mirroring the healthcheck test cases.
/// </summary>
/// <remarks>
/// The only suite that starts **real processes**, worth justifying:
/// the healthcheck exit code decides container health, and both wrong directions hide well —
/// a false unhealthy shows as "container keeps restarting" with the real log already rotated away;
/// a false healthy is "dead but still reported healthy".
/// </remarks>
public class HealthcheckTests
{
    private static string LoopbackOf(HttpClient client) =>
        client.BaseAddress!.Authority;

    [Fact]
    public async Task ReturnsZeroForHealthyService()
    {
        await using ServerFixture f = await ServerFixture.StartAsync();

        int code = Healthcheck.Run(new Dictionary<string, string?>
        {
            ["BMSYNC_ADDR"] = LoopbackOf(f.Client),
        });

        Assert.Equal(Healthcheck.ExitHealthy, code);
    }

    [Fact]
    public void ReturnsNonZeroWhenResponseIsNotThisService()
    {
        // Simulate the reverse proxy routing to another backend: 200 but wrong service
        using var listener = new HttpListener();
        int port = FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _ = Task.Run(() =>
        {
            var ctx = listener.GetContext();
            const string body = """{"ok":true,"service":"something-else"}""";
            byte[] buf = System.Text.Encoding.UTF8.GetBytes(body);
            ctx.Response.OutputStream.Write(buf, 0, buf.Length);
            ctx.Response.Close();
        });

        try
        {
            int code = Healthcheck.Run(new Dictionary<string, string?>
            {
                ["BMSYNC_ADDR"] = $"127.0.0.1:{port}",
            });

            Assert.NotEqual(Healthcheck.ExitHealthy, code);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void ReturnsBadConfigWhenAddressIsInvalid()
    {
        // An unparseable address is an ops problem, unrelated to service health, hence exit 2 not 1 —
        // orchestration seeing 2 can tell "service down" apart from "config mistyped"
        int code = Healthcheck.Run(new Dictionary<string, string?>
        {
            ["BMSYNC_ADDR"] = "not-an-address",
        });

        Assert.Equal(Healthcheck.ExitBadConfig, code);
    }

    [Fact]
    public void ReturnsNonZeroWhenServiceIsDown()
    {
        int code = Healthcheck.Run(new Dictionary<string, string?>
        {
            ["BMSYNC_ADDR"] = $"127.0.0.1:{FreePort()}",
        });

        Assert.NotEqual(Healthcheck.ExitHealthy, code);
    }

    [Fact]
    public async Task WildcardAddressRewrittenToLoopback()
    {
        // Containers listen on 0.0.0.0 / ::, but self-requests from inside must use loopback.
        // Without this rewrite the first healthcheck after container start always fails,
        // so orchestration waits forever and the container never comes up.
        await using ServerFixture f = await ServerFixture.StartAsync();
        string authority = f.Client.BaseAddress!.Authority;
        int colon = authority.LastIndexOf(':');
        string port = authority[(colon + 1)..];

        foreach (string wildcard in new[] { "0.0.0.0", "::", "" })
        {
            int code = Healthcheck.Run(new Dictionary<string, string?>
            {
                ["BMSYNC_ADDR"] = wildcard.Length == 0 ? ":" + port : $"{wildcard}:{port}",
            });

            Assert.Equal(Healthcheck.ExitHealthy, code);
        }
    }

    /// <summary>Find a currently free port.</summary>
    private static int FreePort()
    {
        System.Net.Sockets.TcpListener l = new(System.Net.IPAddress.Loopback, 0);
        l.Start();
        int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}

/// <summary>
/// Process-entry tests, mirroring the process-entry test cases.
/// </summary>
public class CliTests
{
    private static int RunCli(string[] args, Dictionary<string, string?> env)
    {
        foreach ((string k, string? v) in env)
        {
            Environment.SetEnvironmentVariable(k, v);
        }

        try
        {
            return BookmarkSync.Cli.Cli.Run(args);
        }
        finally
        {
            foreach (string k in env.Keys)
            {
                Environment.SetEnvironmentVariable(k, null);
            }
        }
    }

    [Fact]
    public void ConfigCheck_ValidConfigPasses()
    {
        string data = Path.Combine(Path.GetTempPath(), "bmsync-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);

        int code = RunCli(["--config-check"], new Dictionary<string, string?>
        {
            ["BMSYNC_TOKEN"] = new string('t', 40),
            ["BMSYNC_DATA"] = data,
            ["BMSYNC_ADDR"] = "127.0.0.1:0",
        });

        Assert.Equal(0, code);
        Directory.Delete(data, recursive: true);
    }

    [Fact]
    public void ConfigCheck_InvalidConfigExitsNonZero()
    {
        int code = RunCli(["--config-check"], new Dictionary<string, string?>
        {
            ["BMSYNC_TOKEN"] = "too-short",
        });

        // Any non-zero works: RunServer distinguishes 1 (run failed) from 2 (invalid config)
        Assert.NotEqual(0, code);
    }

    /// <summary>
    /// An unusable data dir must also fail: config that "looks right" but crashes on start
    /// is harder to diagnose than a plainly wrong config.
    /// </summary>
    [Fact]
    public void ConfigCheck_FailsWhenDataDirUnusable()
    {
        // Use an existing **file** as the data dir: creating directories is bound to fail
        string f = Path.Combine(Path.GetTempPath(), "bmsync-notdir-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(f, "x");

        int code = RunCli(["--config-check"], new Dictionary<string, string?>
        {
            ["BMSYNC_TOKEN"] = new string('t', 40),
            ["BMSYNC_DATA"] = f,
        });

        Assert.NotEqual(0, code);
        File.Delete(f);
    }

    [Fact]
    public void HealthcheckSubcommandRouting()
    {
        // With no service up it must return non-zero **without** reaching RunServer config loading
        int code = RunCli(["healthcheck"], new Dictionary<string, string?>
        {
            ["BMSYNC_ADDR"] = "127.0.0.1:1",
            ["BMSYNC_TOKEN"] = "unset-short-token",
        });

        Assert.NotEqual(0, code);
    }

    [Theory]
    [InlineData(":8080", "http://*:8080")]
    [InlineData("0.0.0.0:8080", "http://*:8080")]
    [InlineData("127.0.0.1:9999", "http://127.0.0.1:9999")]
    [InlineData("http://example.com:80", "http://example.com:80")]
    public void ListenAddrToUrl(string addr, string want) =>
        Assert.Equal(want, BookmarkSync.Cli.Cli.ToUrl(addr));
}
