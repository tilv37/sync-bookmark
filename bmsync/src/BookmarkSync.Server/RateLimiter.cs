namespace BookmarkSync.Server;

/// <summary>
/// Per-source token bucket.
/// </summary>
/// <remarks>
/// <para>
/// Why it exists: this endpoint faces the public internet (via reverse proxy). Token
/// protection alone still lets a scripted client (or someone brute-forcing tokens)
/// hit /api/sync without bound. Every request does a full merge + disk write,
/// enough to saturate a small VPS.
/// </para>
/// <para>
/// <b>Why not the built-in ASP.NET Core rate-limiting middleware</b>: the built-in
/// <c>AddRateLimiter</c> buckets by endpoint/policy and refills steadily, which differs
/// from this project's "fixed N per minute" (the former allows bursts, the latter does
/// not). A quiet semantic change would break the documented "60/min" promise.
/// </para>
/// </remarks>
public sealed class RateLimiter
{
    private sealed class Bucket
    {
        public int Count;
        public DateTimeOffset Start;
    }

    private readonly Lock _gate = new();
    private int _limit;
    private readonly TimeSpan _window;
    private readonly Dictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private DateTimeOffset _lastGc;

    public RateLimiter(int limit, TimeSpan window)
    {
        _limit = limit;
        _window = window;
        _lastGc = DateTimeOffset.UtcNow;
    }

    /// <summary>Decide whether this request passes, consuming one quota unit.</summary>
    public bool Allow(string key)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            Gc(now);

            if (!_buckets.TryGetValue(key, out Bucket? b) || now - b.Start >= _window)
            {
                _buckets[key] = new Bucket { Count = 1, Start = now };
                return true;
            }

            if (b.Count >= _limit)
            {
                return false;
            }

            b.Count++;
            return true;
        }
    }

    /// <summary>
    /// Reap expired buckets. Triggered manually instead of a background timer: call
    /// frequency equals request frequency, not worth a dedicated background task.
    /// </summary>
    private void Gc(DateTimeOffset now)
    {
        if (now - _lastGc < _window)
        {
            return;
        }

        _lastGc = now;
        List<string> expired = [];
        foreach ((string k, Bucket b) in _buckets)
        {
            if (now - b.Start >= _window)
            {
                expired.Add(k);
            }
        }

        foreach (string k in expired)
        {
            _buckets.Remove(k);
        }
    }

    /// <summary>Tests only: write a bucket directly to simulate a slid window.</summary>
    internal void SeedBucket(string key, int count, DateTimeOffset start)
    {
        lock (_gate)
        {
            _buckets[key] = new Bucket { Count = count, Start = start };
        }
    }

    /// <summary>Tests only: push the last-GC time into the past.</summary>
    internal void ForceGcDue() => _lastGc = DateTimeOffset.UtcNow - _window;

    /// <summary>Tests only: current bucket count.</summary>
    internal int BucketCount
    {
        get
        {
            lock (_gate)
            {
                return _buckets.Count;
            }
        }
    }

    /// <summary>Tests only: shrink the limit to verify "request N+1 is rejected".</summary>
    /// <remarks>
    /// The production limit is 60/min. Testing "rejected when over limit" for real would mean
    /// 61 requests plus a one-minute wait — turning a millisecond test into a minute-long one.
    /// <para>
    /// So the limit is mutable (not readonly) but exposed to tests only: changing the limit
    /// is safe, while "nobody writes this test" is not.
    /// </para>
    /// </remarks>
    internal void OverrideLimitForTest(int limit)
    {
        lock (_gate)
        {
            _limit = limit;
            _buckets.Clear();
        }
    }
}
