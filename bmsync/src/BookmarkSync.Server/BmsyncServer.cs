using BookmarkSync.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BookmarkSync.Server;

/// <summary>
/// 组装 WebApplication：注册服务、装中间件、映射路由。
/// </summary>
/// <remarks>
/// 放在 Server 项目而不是入口项目，是为了<b>能被测试直接调用</b>：
/// 中间件顺序、限流参数、路由表这些东西用 TestServer 单独测是测不到的，
/// 只有把整条管道跑起来才会暴露问题 —— 尤其是"限流在鉴权之前"这种
/// 顺序要求，靠读代码是看不出来的。
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

            // 不读 ASPNETCORE_* 那一套环境变量：这是自建服务，不是跑在
            // 某个通用 .NET 容器模板里，多一套魔法变量只会让"端口从哪来"
            // 变得需要查文档。配置来源只有一个 —— BMSYNC_ADDR。
            EnvironmentName = Environments.Production,
        });

        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new FactoryLoggerProvider(loggerFactory));

        // Kestrel 层的请求体上限，与应用层保持一致。
        // 两层都要设：只设应用层的话，超大请求会先被 Kestrel 读完才开始
        // 应用层的检查，峰值内存就等于请求体大小。
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = HttpLimits.MaxBodyBytes);

        // Store 在这里**急切创建**，而不是注册成 DI 工厂。
        //
        // 两个理由，都不是风格问题：
        //
        // 1. `AddSingleton(async sp => ...)` 会被类型推断绑成
        //    AddSingleton<Task<BookmarkStore>>，结果 BookmarkStore 本身
        //    **没有**被注册。症状是启动一路绿灯，第一个请求才抛
        //    "Unable to resolve service for type 'BookmarkStore'"。
        //    DI 的异步工厂重载存在，但它不带 logger 参数，这里就得自己
        //    在工厂里解析依赖，反而更绕。
        //
        // 2. 存储初始化失败（数据目录不可写、state.json 损坏、schema 不匹配）
        //    **必须**在启动时暴露。注册成工厂的话，服务会正常起来，直到
        //    第一个用户真的来同步才 500 —— 那时用户已经把扩展配好了，
        //    排查成本高得多。Go 版是在 NewServer 里同步做的同一件事。
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

        // ── 中间件 ────────────────────────────────────────────────────
        //
        // 顺序：访问日志在最外层，这样被鉴权拒绝的请求也会留下记录 ——
        // 排查"我的 token 到底有没有被接受"时，那条 401 日志是唯一的线索。

        // 访问日志：刻意**不记录请求体**。body 里含有全部书签 URL，
        // 把它们写进日志等同于制造一份浏览历史副本。反代（NPM）侧的
        // 访问日志同理，只记 URL。
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

        // ── 路由 ──────────────────────────────────────────────────────
        //
        //	GET  /api/health      连通性（免鉴权）
        //	POST /api/sync        上报 + 合并 + 返回权威状态
        //	GET  /api/conflicts   冲突日志
        //	GET  /api/history     历史快照列表
        //
        // handler 把 HttpContext 显式作为第一个参数：Minimal API 支持
        // 把它当参数绑定。不用 IHttpContextAccessor 那种全局单例 ——
        // 那在并行测试下会互相串 HttpContext。

        // /api/health 不鉴权：options 页的"测试连接"需要在用户填完令牌前
        // 就能确认服务活着。它不返回任何书签内容。
        //
        // 用 UseWhen 而不是给每个 Map 调用单独挂中间件：鉴权是"路径相关的
        // 全局策略"，挂在四个 Map 调用里会让"哪些端点受保护"这件事散落
        // 在四处。集中在一处、靠排除法读一遍就知道全貌。
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
/// 把外部 <see cref="ILoggerFactory"/> 适配成 <see cref="ILoggerProvider"/>。
/// </summary>
/// <remarks>
/// WebApplicationBuilder 只收 Provider，不收 Factory。这样适配一层，
/// 入口项目就能用自己那份 loggerFactory（带格式、带等级过滤），
/// 而不必让 Server 项目去决定日志怎么输出。
/// </remarks>
internal sealed class FactoryLoggerProvider(ILoggerFactory factory) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => factory.CreateLogger(categoryName);

    public void Dispose()
    {
        // 不 dispose factory —— 它归调用方所有，可能是进程级的
    }
}
