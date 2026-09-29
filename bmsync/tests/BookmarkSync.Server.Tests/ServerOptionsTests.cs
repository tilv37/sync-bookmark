using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using BookmarkSync.Server;

namespace BookmarkSync.Server.Tests;

/// <summary>
/// 配置解析测试，对应 配置解析的测试用例（同上）。
/// </summary>
/// <remarks>
/// 所有设置都来自环境变量，便于容器化部署。测试传字典而不是改
/// <see cref="Environment"/>：真实环境变量是进程级的并行测试里会互相污染，
/// 而"哪个用例污染了哪个"是种特别难查的偶发失败。
/// </remarks>
public class ServerOptionsTests
{
    /// <summary>构造一份环境变量字典，null 值表示"未设置"。</summary>
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
    public void 默认值()
    {
        ServerOptions cfg = ServerOptions.Load(Env());

        Assert.Equal(":8080", cfg.Addr);
        Assert.Equal("/data", cfg.DataDir);
        Assert.Equal(30, cfg.HistoryKeep);
        Assert.Equal(TimeSpan.FromDays(90), cfg.TombstoneTtl);
    }

    [Fact]
    public void 环境变量覆盖()
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
    /// 配置写错时退回默认值并告警，而不是拒绝启动。
    /// </summary>
    /// <remarks>
    /// 理由见 <see cref="ServerOptions.EnvIntOr"/> 的注释：一个手滑的
    /// <c>HISTORY_KEEP=30天</c> 会让用户以为服务坏了，而实际按默认值跑就能同步。
    /// </remarks>
    [Fact]
    public void 非法数字退回默认值()
    {
        ServerOptions cfg = ServerOptions.Load(Env(
            ("BMSYNC_HISTORY_KEEP", "很多"),
            ("BMSYNC_TOMBSTONE_TTL_DAYS", "abc")));

        Assert.Equal(30, cfg.HistoryKeep);
        Assert.Equal(TimeSpan.FromDays(90), cfg.TombstoneTtl);
    }

    public static TheoryData<string, Dictionary<string, string?>, string> RejectionCases()
    {
        var data = new TheoryData<string, Dictionary<string, string?>, string>();
        var token = new string('x', 40);

        data.Add(
            "token 未设置",
            new Dictionary<string, string?> { ["BMSYNC_TOKEN"] = null },
            "BMSYNC_TOKEN");

        data.Add(
            "token 太短",
            new Dictionary<string, string?> { ["BMSYNC_TOKEN"] = "short" },
            "BMSYNC_TOKEN");

        data.Add(
            "token 恰好 31 字符",
            new Dictionary<string, string?> { ["BMSYNC_TOKEN"] = new string('x', 31) },
            "BMSYNC_TOKEN");

        data.Add(
            "快照份数为 0",
            new Dictionary<string, string?> { ["BMSYNC_TOKEN"] = token, ["BMSYNC_HISTORY_KEEP"] = "0" },
            "HISTORY_KEEP");

        data.Add(
            "墓碑 TTL 不足一天",
            new Dictionary<string, string?> { ["BMSYNC_TOKEN"] = token, ["BMSYNC_TOMBSTONE_TTL_DAYS"] = "0" },
            "TOMBSTONE");

        return data;
    }

    [Theory]
    [MemberData(nameof(RejectionCases))]
    public void 拒绝非法配置(string name, Dictionary<string, string?> env, string wantSub)
    {
        InvalidOperationException ex =
            Assert.Throws<InvalidOperationException>(() => ServerOptions.Load(env));

        Assert.False(string.IsNullOrEmpty(name)); // 场景名用于定位，保留在参数里

        Assert.Contains(wantSub, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>token 恰好 32 字符是下限，必须通过。</summary>
    [Fact]
    public void 接受32字符token()
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
    public void EnvOr_未设置时用默认值()
    {
        Assert.Equal("fallback", ServerOptions.EnvOr(new Dictionary<string, string?>(), "X", "fallback"));
        Assert.Equal("fallback", ServerOptions.EnvOr(new Dictionary<string, string?> { ["X"] = "" }, "X", "fallback"));
        Assert.Equal("explicit", ServerOptions.EnvOr(new Dictionary<string, string?> { ["X"] = "explicit" }, "X", "fallback"));
    }
}

/// <summary>
/// 限流器测试，对应 限流器的测试用例（同上）。
/// </summary>
public class RateLimiterTests
{
    [Fact]
    public void 窗口内超限被拒()
    {
        RateLimiter rl = new(2, TimeSpan.FromMinutes(1));

        Assert.True(rl.Allow("1.1.1.1"));
        Assert.True(rl.Allow("1.1.1.1"));
        Assert.False(rl.Allow("1.1.1.1"));
    }

    [Fact]
    public void 不同来源互不影响()
    {
        RateLimiter rl = new(1, TimeSpan.FromMinutes(1));

        Assert.True(rl.Allow("1.1.1.1"));
        Assert.False(rl.Allow("1.1.1.1"));
        Assert.True(rl.Allow("2.2.2.2"));
    }

    [Fact]
    public void 窗口过期后重新放行()
    {
        RateLimiter rl = new(2, TimeSpan.FromMinutes(1));

        rl.Allow("1.1.1.1");
        rl.Allow("1.1.1.1");
        Assert.False(rl.Allow("1.1.1.1"));

        // 把桶的起点推到过去，模拟窗口已滑过
        rl.SeedBucket("1.1.1.1", 2, DateTimeOffset.UtcNow.AddHours(-1));
        Assert.True(rl.Allow("1.1.1.1"));
    }

    [Fact]
    public void GC清理过期桶()
    {
        RateLimiter rl = new(5, TimeSpan.FromMinutes(1));
        Assert.True(rl.Allow("1.1.1.1"));

        // 把桶与 lastGC 都推到过去，下一次 Allow 就会触发清理
        rl.SeedBucket("1.1.1.1", 1, DateTimeOffset.UtcNow.AddHours(-1));
        rl.ForceGcDue();

        rl.Allow("2.2.2.2");
        rl.Allow("3.3.3.3");

        Assert.Equal(2, rl.BucketCount);
    }

    [Fact]
    public void 未过窗口不清理()
    {
        RateLimiter rl = new(5, TimeSpan.FromHours(1));
        rl.Allow("1.1.1.1");

        rl.Allow("2.2.2.2");

        // 窗口是一个小时，lastGc 才刚初始化，不该触发清理
        Assert.Equal(2, rl.BucketCount);
    }
}

/// <summary>
/// healthcheck 子命令测试，对应
/// healthcheck 子命令的测试用例（同上）。
/// </summary>
/// <remarks>
/// 这是唯一会**起真进程**的一组用例，值得说清为什么值得：
/// healthcheck 的退出码直接决定容器是否被判定为健康，测错的两个方向都很难发现 ——
/// 误判 unhealthy 时症状是"容器反复重启"，而真正的错误日志已经被重启冲掉了；
/// 误判 healthy 则是"挂了还显示正常"。
/// </remarks>
public class HealthcheckTests
{
    private static string LoopbackOf(HttpClient client) =>
        client.BaseAddress!.Authority;

    [Fact]
    public async Task 对健康服务返回0()
    {
        await using ServerFixture f = await ServerFixture.StartAsync();

        int code = Healthcheck.Run(new Dictionary<string, string?>
        {
            ["BMSYNC_ADDR"] = LoopbackOf(f.Client),
        });

        Assert.Equal(Healthcheck.ExitHealthy, code);
    }

    [Fact]
    public void 响应不是本服务时返回非0()
    {
        // 模拟反代把请求路由到了别的后端：返回 200 但 service 不对
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
    public void 地址非法时返回配置错误()
    {
        // 地址无法解析是运维问题，跟服务健康无关，所以退出码是 2 而不是 1 ——
        // 容器编排看到 2 就能区分"服务挂了"和"配置写错了"
        int code = Healthcheck.Run(new Dictionary<string, string?>
        {
            ["BMSYNC_ADDR"] = "这不是地址",
        });

        Assert.Equal(Healthcheck.ExitBadConfig, code);
    }

    [Fact]
    public void 服务未启动时返回非0()
    {
        int code = Healthcheck.Run(new Dictionary<string, string?>
        {
            ["BMSYNC_ADDR"] = $"127.0.0.1:{FreePort()}",
        });

        Assert.NotEqual(Healthcheck.ExitHealthy, code);
    }

    [Fact]
    public async Task wildcard地址被换成回环()
    {
        // 容器里监听的是 0.0.0.0 / ::，但从容器内自请求要连回环地址。
        // 不做这个替换的话，容器启动后第一轮 healthcheck 必然失败，
        // 于是编排系统一直等，容器永远起不来。
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

    /// <summary>找一个当前空闲的端口。</summary>
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
/// 进程入口测试，对应 进程入口的测试用例（同上）。
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
    public void configCheck_合法配置通过()
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
    public void configCheck_非法配置非零退出()
    {
        int code = RunCli(["--config-check"], new Dictionary<string, string?>
        {
            ["BMSYNC_TOKEN"] = "太短",
        });

        // 非 0 即可：具体码在 RunServer 里区分 1（运行失败）与 2（配置无效）
        Assert.NotEqual(0, code);
    }

    /// <summary>
    /// 数据目录不可用时也必须失败：配置"看起来对"但服务起来就崩，
    /// 是比配置写错更难查的一类问题。
    /// </summary>
    [Fact]
    public void configCheck_数据目录不可用时失败()
    {
        // 用一个已存在的**文件**当数据目录：建目录必然失败
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
    public void healthcheck子命令路由()
    {
        // 服务没起时应返回非 0，且**不**走到 RunServer 里的配置加载
        int code = RunCli(["healthcheck"], new Dictionary<string, string?>
        {
            ["BMSYNC_ADDR"] = "127.0.0.1:1",
            ["BMSYNC_TOKEN"] = "未设置的短 token",
        });

        Assert.NotEqual(0, code);
    }

    [Theory]
    [InlineData(":8080", "http://localhost:8080")]
    [InlineData("0.0.0.0:8080", "http://localhost:8080")]
    [InlineData("127.0.0.1:9999", "http://127.0.0.1:9999")]
    [InlineData("http://example.com:80", "http://example.com:80")]
    public void 监听地址转URL(string addr, string want) =>
        Assert.Equal(want, BookmarkSync.Cli.Cli.ToUrl(addr));
}
