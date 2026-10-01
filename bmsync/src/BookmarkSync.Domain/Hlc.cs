using System.Globalization;

namespace BookmarkSync.Domain;

/// <summary>
/// HLC — Hybrid Logical Clock.
/// </summary>
/// <remarks>
/// <para>
/// Paper: Kulkarni et al., "Logical Physical Clocks and Consistent Snapshots
/// in Globally Distributed Databases", 2014.
/// </para>
/// <para>
/// Problem solved: merge uses LWW (Last-Write-Wins), whose correctness fully
/// depends on comparable timestamps. With raw <c>Date.now()</c>, a 5-minute
/// clock skew between two machines lets "my 10:00 bookmark" be overwritten by
/// "your stale 10:03 copy" — silently. That is the worst failure mode for a
/// sync tool.
/// </para>
/// <para>
/// HLC guarantees <b>causal order</b>: once side B has observed an operation
/// from side A, every timestamp B produces afterwards is strictly greater.
/// This holds regardless of the actual wall-clock skew.
/// </para>
/// <para>
/// ── Encoding ──────────────────────────────────────────────────────────<br/>
/// encode(physicalMs, counter) = 13-digit physical millis + '-' + 5-digit counter.<br/>
/// Fixed width, so <b>string lexicographic order == timestamp order</b>;
/// values stay naturally ordered in JSON as well.
/// </para>
/// <para>
/// This file must stay behavior-equivalent with
/// <c>extension/lib/hlc.js</c>. Any divergence makes the merge
/// non-deterministic on the "m exactly equal" branch. The shared contract is
/// <c>test/hlc_vectors.json</c> (cross-validated).
/// </para>
/// </remarks>
public sealed class Hlc
{
    /// <summary>Digits of the physical-millis part.</summary>
    internal const int PhysicalDigits = 13;

    /// <summary>Digits of the logical-counter part.</summary>
    internal const int CounterDigits = 5;

    /// <summary>
    /// Upper bound of the logical counter. On overflow, carry into the
    /// physical part (physicalMs+1, counter=0) to keep the encoding fixed width.
    /// </summary>
    private const int CounterMax = 99_999;

    /// <summary>"Smallest" timestamp, used as a comparison baseline.</summary>
    public const string Zero = "0000000000000-00000";

    /// <summary>Encoding layout, for docs and tests.</summary>
    public const string Layout = "13-digit physical millis + '-' + 5-digit counter, e.g. 1790000000000-00042";

    /// <summary>Exclusive upper bound of the physical-millis part (10^13).</summary>
    private const long PhysicalModulus = 10_000_000_000_000L;

    private readonly object _gate = new();
    private readonly Func<long> _nowFn;

    private long _physicalMs; // Last observed physical time (millis)
    private int _logicalCounter; // Logical ticks within that millis

    /// <summary>Creates an instance using the system wall clock. Use in production.</summary>
    public static Hlc New() => new(() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    /// <summary>
    /// Creates an instance with an injected clock. Tests use this for
    /// deterministic results; the merge path passes its wall-clock in here.
    /// </summary>
    public static Hlc NewWithClock(Func<long> nowFn) => new(nowFn);

    private Hlc(Func<long> nowFn)
    {
        _nowFn = nowFn ?? throw new ArgumentNullException(nameof(nowFn));
    }

    /// <summary>Generates a timestamp for a <b>local</b> event.</summary>
    public string Now()
    {
        lock (_gate)
        {
            long physicalNow = _nowFn();
            if (physicalNow > _physicalMs)
            {
                _physicalMs = physicalNow;
                _logicalCounter = 0;
            }
            else
            {
                _logicalCounter++;
            }

            Normalize();
            return Encode(_physicalMs, _logicalCounter);
        }
    }

    /// <summary>
    /// Advances the local clock after observing a remote timestamp.
    /// </summary>
    /// <remarks>
    /// Call sites: client after receiving the server <c>hlc</c>; server after
    /// scanning the max HLC of all incoming items. See docs/architecture.md §5.
    /// </remarks>
    public string Update(string remote)
    {
        lock (_gate)
        {
            long physicalNow = _nowFn();

            if (!TryDecode(remote, out long remotePhysical, out int remoteCounter))
            {
                // Invalid remote timestamp: fall back to a local tick.
                // No exception: one bad input must not fail the whole sync.
                if (physicalNow > _physicalMs)
                {
                    _physicalMs = physicalNow;
                    _logicalCounter = 0;
                }
                else
                {
                    _logicalCounter++;
                }

                Normalize();
                return Encode(_physicalMs, _logicalCounter);
            }

            long maxObserved = _physicalMs;
            if (physicalNow > maxObserved)
            {
                maxObserved = physicalNow;
            }

            if (remotePhysical > maxObserved)
            {
                maxObserved = remotePhysical;
            }

            // Branch order is load-bearing, not style. A past bug here
            // swapping "max == physicalNow" with "max == remote" makes a tick
            // smaller than the just-received remote stamp when the wall clock
            // catches up to the remote millis, breaking causality and causing
            // silent alternating bookmark loss.
            if (maxObserved == _physicalMs && maxObserved == physicalNow)
            {
                // Local clock and wall clock jointly lead: take max counter + 1.
                if (remoteCounter > _logicalCounter)
                {
                    _logicalCounter = remoteCounter;
                }

                _logicalCounter++;
            }
            else if (maxObserved == _physicalMs)
            {
                // Local clock leads: advance the existing counter.
                _logicalCounter++;
            }
            else if (maxObserved == remotePhysical)
            {
                // Remote stamp leads (including "wall clock just caught up"):
                // continue after the remote counter, otherwise the next local
                // event would sort before the observed remote event.
                _logicalCounter = remoteCounter + 1;
            }
            else
            {
                // maxObserved == physicalNow and strictly ahead: fresh millis.
                _logicalCounter = 0;
            }

            _physicalMs = maxObserved;
            Normalize();
            return Encode(_physicalMs, _logicalCounter);
        }
    }

    /// <summary>
    /// Returns the current timestamp <b>without</b> advancing the counter.
    /// Used for the <c>hlc</c> field sent back to clients.
    /// </summary>
    public string Current()
    {
        lock (_gate)
        {
            return Encode(_physicalMs, _logicalCounter);
        }
    }

    /// <summary>
    /// Absorbs many remote timestamps, advancing past their maximum.
    /// </summary>
    /// <remarks>
    /// The server uses this before merging: every <c>a</c>/<c>m</c> from the
    /// request is fed in, so any timestamp issued afterwards is strictly
    /// greater. See docs/architecture.md §5.
    /// </remarks>
    public string ObserveMany(IEnumerable<string> timestamps)
    {
        ArgumentNullException.ThrowIfNull(timestamps);

        lock (_gate)
        {
            foreach (string timestamp in timestamps)
            {
                if (!TryDecode(timestamp, out long physical, out int counter))
                {
                    continue;
                }

                if (CompareEncoded(physical, counter, _physicalMs, _logicalCounter) > 0)
                {
                    _physicalMs = physical;
                    _logicalCounter = counter;
                }
            }

            return Encode(_physicalMs, _logicalCounter);
        }
    }

    private void Normalize()
    {
        if (_logicalCounter > CounterMax)
        {
            _physicalMs++;
            _logicalCounter = 0;
        }
    }

    /// <summary>Encodes (physical millis, logical counter) as a fixed-width string.</summary>
    public static string Encode(long physicalMs, int counter) =>
        string.Create(CultureInfo.InvariantCulture, $"{physicalMs:D13}-{counter:D5}");

    /// <summary>
    /// Parses an HLC string. Returns false on failure.
    /// </summary>
    /// <remarks>
    /// Strictness matches the legacy Go <c>strconv.ParseUint(s, 10, 64)</c>:
    /// no sign, no surrounding whitespace, no underscore separators.
    /// <see cref="NumberStyles.None"/> (not the default) is required —
    /// the default would also accept " 12" and "+12", a silent cross-port divergence.
    /// </remarks>
    public static bool TryDecode(string? s, out long physicalMs, out int counter)
    {
        physicalMs = 0;
        counter = 0;
        if (s is null || s.Length != PhysicalDigits + 1 + CounterDigits || s[PhysicalDigits] != '-')
        {
            return false;
        }

        if (!long.TryParse(s.AsSpan(0, PhysicalDigits), NumberStyles.None, CultureInfo.InvariantCulture, out physicalMs))
        {
            return false;
        }

        if (!int.TryParse(s.AsSpan(PhysicalDigits + 1), NumberStyles.None, CultureInfo.InvariantCulture, out counter))
        {
            return false;
        }

        return true;
    }

    /// <summary>Returns true for a well-formed HLC string.</summary>
    public static bool IsValid(string? s) => TryDecode(s, out _, out _);

    /// <summary>Compares two HLC values: -1 / 0 / 1.</summary>
    /// <remarks>
    /// Invalid timestamps sort below every valid one, so a corrupted side
    /// always loses and never pollutes the authoritative state. Two invalid
    /// values fall back to ordinal comparison for determinism.
    /// </remarks>
    public static int Compare(string? left, string? right)
    {
        bool leftOk = TryDecode(left, out long leftPhysical, out int leftCounter);
        bool rightOk = TryDecode(right, out long rightPhysical, out int rightCounter);

        if (!leftOk && !rightOk)
        {
            return string.CompareOrdinal(left ?? string.Empty, right ?? string.Empty);
        }

        if (!leftOk)
        {
            return -1;
        }

        if (!rightOk)
        {
            return 1;
        }

        return CompareEncoded(leftPhysical, leftCounter, rightPhysical, rightCounter);
    }

    /// <summary>Compares already-decoded (physical, counter) pairs without re-parsing.</summary>
    public static int CompareEncoded(long leftPhysical, int leftCounter, long rightPhysical, int rightCounter)
    {
        if (leftPhysical < rightPhysical)
        {
            return -1;
        }

        if (leftPhysical > rightPhysical)
        {
            return 1;
        }

        if (leftCounter < rightCounter)
        {
            return -1;
        }

        if (leftCounter > rightCounter)
        {
            return 1;
        }

        return 0;
    }

    /// <summary>Returns the maximum timestamp in a sequence; empty yields <see cref="Zero"/>.</summary>
    public static string MaxHlc(IEnumerable<string> list)
    {
        ArgumentNullException.ThrowIfNull(list);
        string output = Zero;
        foreach (string candidate in list)
        {
            if (Compare(candidate, output) > 0)
            {
                output = candidate;
            }
        }

        return output;
    }

    /// <summary>
    /// Params overload of <see cref="MaxHlc(IEnumerable{string})"/>.
    /// </summary>
    public static string MaxHlc(params string[] list) => MaxHlc((IEnumerable<string>)list);

    /// <summary>Exclusive upper bound of physical millis, for tests and docs.</summary>
    internal static long PhysicalModulusInternal => PhysicalModulus;

    /// <summary>
    /// Test-only: sets internal state directly.
    /// </summary>
    /// <remarks>
    /// Rationale: reaching the counter-overflow branch (99999 → carry) through
    /// the public API would require ~100k <see cref="Now"/> calls. A slow test
    /// becomes an excuse for missing edge coverage; direct setup keeps the
    /// boundary test in the millisecond range.
    /// <para>
    /// <c>internal</c> + <c>InternalsVisibleTo</c>: visible to tests only,
    /// unreachable from production code.
    /// </para>
    /// </remarks>
    internal void SetStateForTest(long physicalMs, int counter)
    {
        lock (_gate)
        {
            _physicalMs = physicalMs;
            _logicalCounter = counter;
        }
    }
}
