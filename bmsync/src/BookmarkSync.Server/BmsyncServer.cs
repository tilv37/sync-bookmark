using BookmarkSync.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BookmarkSync.Server;

/// <summary>
/// Assemble the WebApplication: register services, install middleware, map routes.
/// </summary>
/// <remarks>
/// Lives in the Server project (not the entry point) so <b>tests can call it directly</b>:
/// middleware order, rate-limit params, and the route table cannot be covered by testing handlers alone;
/// only the full pipeline exposes problems — especially ordering rules like "rate limit before auth"
/// that are invisible from reading code.
/// </remarks>
public static class BmsyncServer
{
    public static WebApplication Build(
        ServerOptions options, ILoggerFactory loggerFactory, string[]? args = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args ?? [],

            // Ignore the ASPNETCORE_* env vars: this is a self-hosted service, not part of
            // a generic .NET container template, and another set of magic variables would make
            // "where does the port come from" a docs lookup. There is exactly one config source: BMSYNC_ADDR.
            EnvironmentName = Environments.Production,
        });

        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new FactoryLoggerProvider(loggerFactory));

        // Kestrel-level body cap, kept in sync with the app-level cap.
        // Both layers are needed: with only the app-level check, Kestrel would fully
        // buffer an oversized request first, so peak memory equals the body size.
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = HttpLimits.MaxBodyBytes);

        // The store is created **eagerly** here, not registered as a DI factory.
        //
        // Two reasons, neither stylistic:
        //
        // 1. `AddSingleton(async sp => ...)` infers to AddSingleton<Task<BookmarkStore>>,
        //    leaving BookmarkStore itself **unregistered**. Startup looks green and the
        //    first request throws "Unable to resolve service for type 'BookmarkStore'".
        //    An async DI factory overload exists but takes no logger, forcing manual
        //    dependency resolution inside the factory — more convoluted.
        //
        // 2. Storage init failures (unwritable data dir, corrupt state.json, schema
        //    mismatch) **must** surface at startup. As a factory the service would come up
        //    fine and 500 only on the first real sync — after the user already configured
        //    the extension, when debugging costs far more.
        var store = BookmarkStore.CreateAsync(
            new StoreOptions
            {
                DataDir = options.DataDir,
                HistoryKeep = options.HistoryKeep,
                TombstoneTtl = options.TombstoneTtl,
            },
            loggerFactory.CreateLogger<BookmarkStore>()).GetAwaiter().GetResult();

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(
            new RateLimiter(HttpLimits.RateLimitPerMinute, TimeSpan.FromMinutes(1)));
        builder.Services.AddSingleton<ApiEndpoints>();

        WebApplication app = builder.Build();

        ILogger log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("bmsync");
        RateLimiter limiter = app.Services.GetRequiredService<RateLimiter>();

        // ── Middleware ────────────────────────────────────────────────
        //
        // Order: access log outermost so auth-rejected requests are still logged —
        // when debugging "was my token accepted", that 401 line is the only clue.

        // Access log deliberately records **no request body**. The body holds every bookmark
        // URL; logging it would create a browsing-history copy. Same rule applies to the
        // reverse-proxy (NPM) access log: URL only.
        app.Use(async (ctx, next) =>
        {
            if (!ctx.Request.Path.StartsWithSegments("/api/"))
            {
                await next(ctx).ConfigureAwait(false);
                return;
            }

            long start = Environment.TickCount64;
            try
            {
                await next(ctx).ConfigureAwait(false);
            }
            finally
            {
                log.LogInformation(
                    "http {Method} {Path} {Status} {Ms}ms",
                    ctx.Request.Method,
                    ctx.Request.Path.Value,
                    ctx.Response.StatusCode,
                    Environment.TickCount64 - start);
            }
        });

        // ── Routes ────────────────────────────────────────────────────
        //
        //	GET  /api/health      connectivity (no auth)
        //	POST /api/sync        upload + merge + return authoritative state
        //	GET  /api/conflicts   conflict log
        //	GET  /api/history     history snapshot list
        //
        // Handlers take HttpContext explicitly as the first parameter: Minimal API supports
        // binding it as an argument. No IHttpContextAccessor-style global singleton —
        // that leaks HttpContext across parallel tests.

        // /api/health skips auth: the options page "test connection" must confirm the
        // service is alive before the user finishes entering the token. It returns no
        // bookmark content.
        //
        // UseWhen instead of per-Map middleware: auth is a "path-dependent global policy";
        // spreading it over four Map calls scatters "which endpoints are protected".
        // Centralized here, one exclusion read shows the whole picture.
        app.UseWhen(
            ctx => !ctx.Request.Path.Equals("/api/health", StringComparison.OrdinalIgnoreCase),
            branch => branch.Use(Auth.Protect(options, limiter, log)));

        app.MapGet("/api/health", (HttpContext ctx, ApiEndpoints api) => api.HandleHealthAsync(ctx));
        app.MapPost("/api/sync", (HttpContext ctx, ApiEndpoints api) => api.HandleSyncAsync(ctx));
        app.MapGet("/api/conflicts", (HttpContext ctx, ApiEndpoints api) => api.HandleConflictsAsync(ctx));
        app.MapGet("/api/history", (HttpContext ctx, ApiEndpoints api) => api.HandleHistoryAsync(ctx));

        return app;
    }
}

/// <summary>
/// Adapt an external <see cref="ILoggerFactory"/> to <see cref="ILoggerProvider"/>.
/// </summary>
/// <remarks>
/// WebApplicationBuilder only accepts providers, not factories. With this adapter,
/// the entry project keeps its own loggerFactory (format, level filtering),
/// instead of letting the Server project decide log output.
/// </remarks>
internal sealed class FactoryLoggerProvider(ILoggerFactory factory) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => factory.CreateLogger(categoryName);

    public void Dispose()
    {
        // Do not dispose the factory — owned by the caller, possibly process-wide.
    }
}
