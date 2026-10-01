namespace BookmarkSync.Server;

/// <summary>
/// All server runtime config. Sourced entirely from env vars for containerized deploys.
/// </summary>
public sealed class ServerOptions
{
    /// <summary>
    /// HTTP listen address, e.g. ":8080". May come from a different source than
    /// <see cref="DataDir"/> (the former from BMSYNC_ADDR/flag, the latter from BMSYNC_DATA), so kept separate.
    /// </summary>
    public required string Addr { get; init; }

    /// <summary>Bearer token; clients must send it on every request Authorization header.</summary>
    public required string Token { get; init; }

    /// <summary>Directory holding state.json / conflicts.json / history/.</summary>
    public required string DataDir { get; init; }

    /// <summary>Number of history snapshots to keep.</summary>
    public int HistoryKeep { get; init; }

    /// <summary>How long tombstones (deleted items) are kept before GC reaps them.</summary>
    public TimeSpan TombstoneTtl { get; init; }

    public const string DefaultAddr = ":8080";
    public const string DefaultDataDir = "/data";
    public const int DefaultHistoryKeep = 30;
    public const int MinTokenLen = 32;

    /// <summary>Service name self-reported at /api/health.</summary>
    public const string ServiceName = "bmsync";

    /// <summary>
    /// Load and validate config from environment variables.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown on invalid config; callers should refuse to start.</exception>
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
                $"BMSYNC_TOKEN is missing or shorter than {MinTokenLen} chars (got {cfg.Token.Length}) — set it to the output of `openssl rand -hex 32`");
        }

        if (cfg.HistoryKeep < 1)
        {
            throw new InvalidOperationException($"BMSYNC_HISTORY_KEEP must be >= 1, got {cfg.HistoryKeep}");
        }

        if (cfg.TombstoneTtl < TimeSpan.FromDays(1))
        {
            throw new InvalidOperationException(
                $"BMSYNC_TOMBSTONE_TTL_DAYS too short ({cfg.TombstoneTtl}), cleaning tombstones too early breaks delete propagation");
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

    /// <summary>Env var wins; fall back to the default when unset.</summary>
    public static string EnvOr(IReadOnlyDictionary<string, string?> env, string key, string fallback) =>
        env.TryGetValue(key, out string? v) && !string.IsNullOrEmpty(v) ? v : fallback;

    /// <summary>
    /// Read an integer env var.
    /// </summary>
    /// <remarks>
    /// On parse failure <b>fall back to the default</b> instead of throwing: a typoed
    /// config should degrade rather than prevent startup. Same rationale as the Go
    /// <c>envIntOr</c>: a slip like BMSYNC_HISTORY_KEEP=30d would look like a broken
    /// service when running on defaults would sync fine.
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

    /// <summary>Read a "days" env var and convert it to <see cref="TimeSpan"/>.</summary>
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
        Console.Error.WriteLine($"env var {key}=\"{value}\" is not a valid integer, using default {fallback}");
}
