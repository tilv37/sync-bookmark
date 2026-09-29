using BookmarkSync.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;

namespace BookmarkSync.Cli;

/// <summary>
/// 进程入口：启动 Kestrel、注册中间件与路由、优雅关闭、healthcheck 子命令。
/// </summary>
/// <remarks>
/// 本项目只做两件事：启动进程、优雅关闭。所有实际逻辑在 Server / Store / Domain
/// 三个库里，按依赖方向分层：
/// <code>
/// Cli → Server → Store → Domain
/// </code>
/// </remarks>
public static class Cli
{
    public static int Run(string[] args)
    {
        // healthcheck 是子命令而不是 flag：FROM scratch 镜像里没有 shell，
        // compose 的 healthcheck 只能写成 ["CMD", "/bmsync", "healthcheck"]，
        // 那种形式没法带 flag。详见 Server/Healthcheck.cs。
        if (args.Length > 0 && args[0] == "healthcheck")
        {
            return Healthcheck.Run();
        }

        return RunServer(args);
    }

    private static int RunServer(string[] args)
    {
        using ILoggerFactory loggerFactory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Information);
            b.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
            });
        });
        ILogger log = loggerFactory.CreateLogger("bmsync");

        // 只校验配置和数据目录，不监听端口。
        // 它的用途是"部署出问题时确认是配置错还是程序错"，所以**绝不能**
        // 真去 bind —— 否则主进程会因端口被占而起不来，问题从"配置错"
        // 变成"端口冲突"，反而更难查。
        bool configCheck = args.Contains("--config-check");

        ServerOptions cfg;
        try
        {
            cfg = ServerOptions.Load();
        }
        catch (InvalidOperationException ex)
        {
            log.LogCritical("配置无效: {Message}", ex.Message);
            return 2;
        }

        WebApplication app;
        try
        {
            app = BmsyncServer.Build(cfg, loggerFactory, args);
        }
        catch (Exception ex)
        {
            // Store 初始化失败（数据目录不可写、state.json 损坏、schema 不匹配）
            // 在这里才发生，且**必须**让进程起不来：带着半残状态服务只会放大问题。
            log.LogCritical("初始化失败: {Message}", ex.Message);
            return 2;
        }

        if (configCheck)
        {
            Console.WriteLine("config ok");
            return 0;
        }

        log.LogInformation(
            "config loaded addr={Addr} data={Data} historyKeep={HistoryKeep} tombstoneTtl={Ttl}",
            cfg.Addr, cfg.DataDir, cfg.HistoryKeep, cfg.TombstoneTtl);

        // Kestrel 的监听地址用 BMSYNC_ADDR。它同时被 healthcheck 读取，
        // 所以两端必须一致 —— 改了一处忘了另一处，容器会永远 unhealthy。
        app.Urls.Clear();
        app.Urls.Add(ToUrl(cfg.Addr));

        try
        {
            app.Run();
            return 0;
        }
        catch (Exception ex)
        {
            log.LogCritical("运行失败: {Message}", ex.Message);
            return 1;
        }
    }

    /// <summary>把 ":8080" / "0.0.0.0:8080" 这类监听地址转成 Kestrel 认的 URL。</summary>
    internal static string ToUrl(string addr)
    {
        if (addr.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || addr.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return addr;
        }

        int colon = addr.LastIndexOf(':');
        if (colon < 0)
        {
            return "http://" + addr;
        }

        string host = addr[..colon];
        string port = addr[(colon + 1)..];

        if (host.Length == 0 || host is "0.0.0.0" or "::" or "[::]")
        {
            // 必须绑所有网卡（Kestrel 认 http://*:port）：Docker 端口映射
            // DNAT 到容器的 eth0 IP，绑 localhost 的话映射进来的连接会被
            // RST，而容器内的 healthcheck 自查走 localhost 不受影响 ——
            // 于是出现“healthy 但外部访问不了”。之前的 localhost 写法
            // 只在裸机直跑时成立。
            return $"http://*:{port}";
        }
        else if (host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('['))
        {
            host = "[" + host + "]";
        }

        return $"http://{host}:{port}";
    }
}

/// <summary>进程真正的 Main。只做一件事：把返回值交给操作系统。</summary>
public static class Program
{
    public static int Main(string[] args) => Cli.Run(args);
}
