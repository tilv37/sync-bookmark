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
/// Spin up a full real pipeline instead of testing handlers in isolation.
/// </summary>
/// <remarks>
/// <para>
/// Not a "stricter way to test" but the <b>only way that catches</b> middleware order, rate
/// limits, auth, and Content-Type — exactly the "handlers are correct but the pipeline is
/// miswired" class. One real ASP.NET Core incident was
/// <c>AddSingleton(async sp =&gt; ...)</c> inferring to
/// <c>Task&lt;T&gt;</c> — every handler unit test green, the service never registered in DI,
/// exploding only on the first request. Unit tests structurally cannot catch that.
/// </para>
/// <para>
/// A <b>real Kestrel</b> instead of TestServer: TestServer needs a
/// <c>WebApplicationFactory</c> wrapper to own the host, while this project's
/// <c>BmsyncServer.Build</c> calls <c>WebApplication.CreateBuilder</c> itself — forcing
/// TestServer yields an <c>InvalidCastException</c> unrelated to the logic under test.
/// Real Kestrel costs port allocation (port 0 lets the OS choose) and buys "production code
/// testable with zero changes".
/// </para>
/// </remarks>
internal sealed class ServerFixture : IAsyncDisposable
{
    public const string Token = "tttttttttttttttttttttttttttttttttttttttt"; // 40 t chars

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
            // Port 0 = let the OS pick a free port. Parallel tests on a fixed port would
            // inevitably collide, and the symptom (another test's 500) would be misread
            // as "broken code".
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

        // The rate limit can be overridden by tests. The default 60/min is too slow for "the 61st
        // request is rejected" (it would need a real minute of waiting), so shrinking is allowed here.
        if (rateLimitPerMinute is { } limit)
        {
            var rl = app.Services.GetRequiredService<RateLimiter>();
            ReseedRateLimiter(rl, limit);
        }

        await app.StartAsync();

        // The actual port-0 binding must be read back from server features:
        // app.Urls still holds the "http://127.0.0.1:0" placeholder.
        // Without this step HttpClient dials port 0, gets "connection refused",
        // and it looks like the service never started.
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
    /// Replace the rate limiter quota.
    /// </summary>
    /// <remarks>
    /// <see cref="RateLimiter"/>'s quota used to be a construction-time readonly field, so
    /// swapping the whole instance is more honest than "mutating a field" — the latter would need
    /// a test-only setter on a production type, weakening the "quota is immutable" property.
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
            // ApiJson.StrictRequest, not BmsyncJson.Storage: the request side must
            // serialize SyncRequest without source generation bypassing the custom converters.
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
            // Test cleanup failures must not fail tests — on Windows that is just handle-release timing
        }
    }
}

/// <summary>Test scaffolding for the HTTP layer.</summary>
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
    /// Build a /api/sync request body.
    /// </summary>
    /// <remarks>
    /// The return type is <see cref="State"/>, not an anonymous object. PublishAot disables
    /// reflection-based JSON, and generated contexts only know **compile-time registered types** —
    /// anonymous types are compiler-invented, so no context knows them.
    /// The symptom is NotSupportedException reporting
    /// "&lt;&gt;f__AnonymousType0`3[...]", pointing in a completely wrong direction
    /// (it looks like a framework bug, but the test code shape is at fault).
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
