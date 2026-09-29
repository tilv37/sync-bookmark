namespace BookmarkSync.Server;

/// <summary>
/// 服务端的全部运行期配置。全部来自环境变量，便于容器化部署。
/// </summary>
public sealed class ServerOptions
{
    /// <summary>
    /// HTTP 监听地址，如 ":8080"。可能与 <see cref="DataDir"/> 分属不同来源
    /// （前者来自 BMSYNC_ADDR/flag，后者来自 BMSYNC_DATA），故单独保留。
    /// </summary>
    public required string Addr { get; init; }

    /// <summary>Bearer 令牌，客户端必须在每个请求的 Authorization 头携带。</summary>
    public required string Token { get; init; }

    /// <summary>state.json / conflicts.json / history/ 所在目录。</summary>
    public required string DataDir { get; init; }

    /// <summary>保留的历史快照份数。</summary>
    public int HistoryKeep { get; init; }

    /// <summary>墓碑（已删除项）的保留时长，超期后由 GC 清理。</summary>
    public TimeSpan TombstoneTtl { get; init; }

    public const string DefaultAddr = ":8080";
    public const string DefaultDataDir = "/data";
    public const int DefaultHistoryKeep = 30;
    public const int MinTokenLen = 32;

    /// <summary>本服务在 /api/health 里自报的名字。</summary>
    public const string ServiceName = "bmsync";

    /// <summary>
    /// 从环境变量读取并校验配置。
    /// </summary>
    /// <exception cref="InvalidOperationException">配置非法时抛出，调用方应据此拒绝启动。</exception>
    public static ServerOptions Load(IReadOnlyDictionary<string, string?>? env = null)
    {
        IReadOnlyDictionary<string, string?> e = env ?? ReadProcessEnv();

        var cfg = new ServerOptions
        {
            Addr = EnvOr(e, "BMSYNC_ADDR", DefaultAddr),
            Token = e.TryGetValue("BMSYNC_TOKEN", out string? t) ? t ?? string.Empty : string.Empty,
            DataDir = EnvOr(e, "BMSYNC_DATA", DefaultDataDir),
            HistoryKeep = EnvIntOr(e, "BMSYNC_HISTORY_KEEP", DefaultHistoryKeep),
            TombstoneTtl = EnvDurationDaysOr(e, "BMSYNC_TOMBSTONE_TTL_DAYS", TimeSpan.FromDays(90)),
        };

        if (cfg.Token.Length < MinTokenLen)
        {
            throw new InvalidOperationException(
                $"BMSYNC_TOKEN 未设置或短于 {MinTokenLen} 字符（当前 {cfg.Token.Length}）" +
                "——请设置 `openssl rand -hex 32` 的输出");
        }

        if (cfg.HistoryKeep < 1)
        {
            throw new InvalidOperationException($"BMSYNC_HISTORY_KEEP 必须 ≥ 1，当前 {cfg.HistoryKeep}");
        }

        if (cfg.TombstoneTtl < TimeSpan.FromDays(1))
        {
            throw new InvalidOperationException(
                $"BMSYNC_TOMBSTONE_TTL_DAYS 过短（{cfg.TombstoneTtl}），墓碑太早清理会导致删除同步失效");
        }

        return cfg;
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

    /// <summary>环境变量优先，未设置时用默认值。</summary>
    public static string EnvOr(IReadOnlyDictionary<string, string?> env, string key, string fallback) =>
        env.TryGetValue(key, out string? v) && !string.IsNullOrEmpty(v) ? v : fallback;

    /// <summary>
    /// 读整数环境变量。
    /// </summary>
    /// <remarks>
    /// 解析失败时<b>退回默认值</b>而不是抛异常：配置写错时宁可行为不理想，
    /// 也不要起不来。理由同 Go 版 <c>envIntOr</c>：一个手滑的
    /// BMSYNC_HISTORY_KEEP=30天 会让用户以为服务坏了，而实际只要按默认值
    /// 跑就能同步。
    /// </remarks>
    public static int EnvIntOr(IReadOnlyDictionary<string, string?> env, string key, int fallback)
    {
        if (!env.TryGetValue(key, out string? v) || string.IsNullOrEmpty(v))
        {
            return fallback;
        }

        if (int.TryParse(v, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int n))
        {
            return n;
        }

        WarnBadNumber(key, v, fallback.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return fallback;
    }

    /// <summary>读"天数"环境变量，转换成 <see cref="TimeSpan"/>。</summary>
    public static TimeSpan EnvDurationDaysOr(
        IReadOnlyDictionary<string, string?> env, string key, TimeSpan fallback)
    {
        if (!env.TryGetValue(key, out string? v) || string.IsNullOrEmpty(v))
        {
            return fallback;
        }

        if (int.TryParse(v, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int days))
        {
            return TimeSpan.FromDays(days);
        }

        WarnBadNumber(key, v, fallback.ToString());
        return fallback;
    }

    private static void WarnBadNumber(string key, string value, string fallback) =>
        Console.Error.WriteLine($"环境变量 {key}=\"{value}\" 不是合法整数，使用默认值 {fallback}");
}
