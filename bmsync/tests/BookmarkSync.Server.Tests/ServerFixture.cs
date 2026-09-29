using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BookmarkSync.Domain;
using BookmarkSync.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BookmarkSync.Server.Tests;

/// <summary>
/// 用 TestServer 起一整条真实管道，而不是单独测处理器。
/// </summary>
/// <remarks>
/// <para>
/// 这不是"更严格的测法"，而是<b>唯一测得到</b>的测法：中间件顺序、限流、
/// 鉴权、Content-Type 这些恰恰是"处理器本身完全正确、但管道装错了"
/// 的部分。ASP.NET Core 的一次真实事故就是
/// <c>AddSingleton(async sp =&gt; ...)</c> 被推断成
/// <c>Task&lt;T&gt;</c> —— 所有处理器单测全绿，DI 里根本没有注册那个服务，
/// 直到第一个请求才炸。单元测试结构上无法发现这类问题。
/// </para>
/// <para>
/// 起<b>真 Kestrel</b> 而不用 TestServer：TestServer 需要包一层
/// <c>WebApplicationFactory</c> 才能接管 host，而本项目的
/// <c>BmsyncServer.Build</c> 是自己 <c>WebApplication.CreateBuilder</c> 的，
/// 硬去取 TestServer 会得到一个与被测逻辑毫无关系的
/// <c>InvalidCastException</c>。起真 Kestrel 的代价是端口分配（用端口 0
/// 让系统选），换来的是"生产代码零改动就能被测"。
/// </para>
/// </remarks>
internal sealed class ServerFixture : IAsyncDisposable
{
    public const string Token = "tttttttttttttttttttttttttttttttttttttttt"; // 40 个 t

    private readonly WebApplication _app;

    private ServerFixture(WebApplication app, HttpClient client, string dataDir)
    {
        _app = app;
        Client = client;
        DataDir = dataDir;
    }

    public HttpClient Client { get; }

    public string DataDir { get; }

    public static async Task<ServerFixture> StartAsync(
        string? token = null,
        int? rateLimitPerMinute = null)
    {
        string dataDir = Path.Combine(Path.GetTempPath(), "bmsync-srv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);

        ServerOptions options = new()
        {
            // 端口 0 = 让操作系统分配一个空闲端口。多个测试并行跑时
            // 写死端口必然撞车，而撞车的症状（另一个测试的 500）会被
            // 误读成"代码有问题"。
            Addr = "127.0.0.1:0",
            Token = token ?? Token,
            DataDir = dataDir,
            HistoryKeep = 3,
            TombstoneTtl = TimeSpan.FromDays(90),
        };

        WebApplication app = BmsyncServer.Build(
            options, NullLoggerFactory.Instance, args: []);

        app.Urls.Clear();
        app.Urls.Add("http://127.0.0.1:0");

        // 限流值可被测试覆盖。默认 60 次/分钟对"测第 61 次被拒"来说太慢
        // （要用真实时间等一分钟），所以这里允许把它调小。
        if (rateLimitPerMinute is { } limit)
        {
            var rl = app.Services.GetRequiredService<RateLimiter>();
            ReseedRateLimiter(rl, limit);
        }

        await app.StartAsync();

        // 端口 0 的实际绑定结果要从 server features 里读回来：
        // app.Urls 里留的还是 "http://127.0.0.1:0" 那个占位串。
        // 少了这一步，HttpClient 会去连 0 号端口，报"连接被拒绝"，
        // 看起来像服务没起来。
        string baseAddress = app.Urls.FirstOrDefault() ?? "http://127.0.0.1:0";
        if (Uri.TryCreate(baseAddress, UriKind.Absolute, out Uri? parsed)
            && parsed.Port == 0
            && app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>() is { Addresses.Count: > 0 } f)
        {
            baseAddress = f.Addresses.First();
        }

        return new ServerFixture(app, new HttpClient { BaseAddress = new Uri(baseAddress) }, dataDir);
    }

    /// <summary>
    /// 替换限流器的额度。
    /// </summary>
    /// <remarks>
    /// <see cref="RateLimiter"/> 的额度是构造时定下的 readonly 字段，
    /// 换掉整个实例比"改字段"诚实 —— 后者需要在生产类型上留一个只给测试用的
    /// setter，那会削弱"额度不可变"这个性质。
    /// </remarks>
    private static void ReseedRateLimiter(RateLimiter rl, int limit) =>
        rl.OverrideLimitForTest(limit);

    public HttpRequestMessage Request(HttpMethod method, string path, string? token = Token, bool withToken = true)
    {
        HttpRequestMessage req = new(method, path);
        if (withToken && token is not null)
        {
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        return req;
    }

    public async Task<(HttpStatusCode Status, string Body)> PostSyncAsync(
        SyncRequest? payload, string? token = Token, bool withToken = true, string? rawBody = null)
    {
        using HttpRequestMessage req = Request(HttpMethod.Post, "/api/sync", token, withToken);
        if (rawBody is not null)
        {
            req.Content = new StringContent(rawBody, Encoding.UTF8, "application/json");
        }
        else if (payload is not null)
        {
            // 用 ApiJson.StrictRequest 而不是 BmsyncJson.Storage：请求侧要
            // 能序列化 SyncRequest，且不该被源生成绕开自定义转换器。
            req.Content = new StringContent(
                JsonSerializer.Serialize(payload, ApiJson.Response), Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage resp = await Client.SendAsync(req);
        return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }

    public async Task<(HttpStatusCode Status, string Body)> GetAsync(
        string path, string? token = Token, bool withToken = true)
    {
        using HttpRequestMessage req = Request(HttpMethod.Get, path, token, withToken);
        using HttpResponseMessage resp = await Client.SendAsync(req);
        return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();

        try
        {
            Directory.Delete(DataDir, recursive: true);
        }
        catch (IOException)
        {
            // 测试清理失败不该让测试失败 —— 那是 Windows 上文件句柄的释放时机问题
        }
    }
}

/// <summary>HTTP 层的测试脚手架。</summary>
internal static class Fx
{
    public const string Key1 = "11111111111111111111111111111111";
    public const string Key2 = "22222222222222222222222222222222";

    public static Item Bookmark(string parent, string title, string url, long ms = 100, int c = 0) => new()
    {
        P = parent, T = ItemTypes.Bookmark, N = title, U = url,
        M = Hlc.Encode(ms, c), A = Hlc.Encode(ms, 0),
    };

    public static Item Folder(string parent, string title, long ms = 100, int c = 0) => new()
    {
        P = parent, T = ItemTypes.Folder, N = title,
        M = Hlc.Encode(ms, c), A = Hlc.Encode(ms, 0),
    };

    public static Dictionary<string, Item> Items(params (string Key, Item Item)[] items)
    {
        var d = State.NewItemsDictionary();
        foreach ((string k, Item it) in items)
        {
            d[k] = it;
        }

        return d;
    }

    /// <summary>
    /// 构造一个 /api/sync 请求体。
    /// </summary>
    /// <remarks>
    /// 返回类型是 <see cref="State"/> 而不是匿名对象。原因是 PublishAot 关掉了
    /// 反射式 JSON，而源生成上下文**只认编译期登记过的类型** ——
    /// 匿名类型是编译时凭空生成的，任何上下文都不认识它。
    /// 症状是 NotSupportedException，而且报的是
    /// "&lt;&gt;f__AnonymousType0`3[...]"，指向完全错误的方向
    /// （看起来像框架的 bug，其实是测试代码的形状不对）。
    /// </remarks>
    public static SyncRequest SyncBody(
        Dictionary<string, Item> items,
        string device = "dev-a",
        Dictionary<string, string>? @base = null) =>
        new()
        {
            Device = device,
            State = new State { V = Schema.Version, Items = items },
            Base = @base ?? new Dictionary<string, string>(StringComparer.Ordinal),
        };
}
