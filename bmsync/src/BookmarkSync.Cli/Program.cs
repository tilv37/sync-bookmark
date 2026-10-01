using BookmarkSync.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;

namespace BookmarkSync.Cli;

/// <summary>
/// Process entry: start Kestrel, register middleware and routes, graceful shutdown, healthcheck subcommand.
/// </summary>
/// <remarks>
/// This project does two things only: start the process and shut down gracefully. All real logic
/// lives in the Server / Store / Domain libraries, layered by dependency:
/// <code>
/// Cli → Server → Store → Domain
/// </code>
/// </remarks>
public static class Cli
{
    public static int Run(string[] args)
    {
        // healthcheck is a subcommand, not a flag: the FROM scratch image has no shell,
        // so compose healthcheck can only be ["CMD", "/bmsync", "healthcheck"],
        // which cannot pass flags. See Server/Healthcheck.cs.
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

        // Only validate config and data dir, do not listen on any port.
        // Its purpose is telling "config wrong vs program wrong" during broken deploys, so it must
        // **never** actually bind — otherwise the main process fails on a taken port and the problem
        // morphs from "bad config" into "port conflict", which is harder to trace.
        bool configCheck = args.Contains("--config-check");

        ServerOptions cfg;
        try
        {
            cfg = ServerOptions.Load();
        }
        catch (InvalidOperationException ex)
        {
            log.LogCritical("Invalid config: {Message}", ex.Message);
            return 2;
        }

        WebApplication app;
        try
        {
            app = BmsyncServer.Build(cfg, loggerFactory, args);
        }
        catch (Exception ex)
        {
            // Store init failures (unwritable data dir, corrupt state.json, schema mismatch)
            // surface here, and **must** keep the process down: serving on half-broken state only amplifies damage.
            log.LogCritical("Init failed: {Message}", ex.Message);
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

        // Kestrel listens on BMSYNC_ADDR. healthcheck reads the same value,
        // so both sides must agree — change one and forget the other and the container stays unhealthy forever.
        app.Urls.Clear();
        app.Urls.Add(ToUrl(cfg.Addr));

        try
        {
            app.Run();
            return 0;
        }
        catch (Exception ex)
        {
            log.LogCritical("Run failed: {Message}", ex.Message);
            return 1;
        }
    }

    /// <summary>Convert listen addresses like ":8080" / "0.0.0.0:8080" into Kestrel URLs.</summary>
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
            // Must bind all interfaces (Kestrel wants http://*:port): Docker port mapping
            // DNATs to the container eth0 IP, so binding localhost makes mapped connections
            // RST while the in-container healthcheck self-probe over localhost still passes —
            // i.e. "healthy but unreachable from outside". The old localhost form only worked
            // when running on bare metal.
            return $"http://*:{port}";
        }
        else if (host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('['))
        {
            host = "[" + host + "]";
        }

        return $"http://{host}:{port}";
    }
}

/// <summary>The real process Main. Does one thing: hand the exit code to the OS.</summary>
public static class Program
{
    public static int Main(string[] args) => Cli.Run(args);
}
