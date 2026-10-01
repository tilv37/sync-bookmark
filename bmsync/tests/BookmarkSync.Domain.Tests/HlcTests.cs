using System.Text.Json;
using System.Text.Json.Serialization;

namespace BookmarkSync.Domain.Tests;

/// <summary>
/// A manually advanced clock keeping HLC tests fully deterministic, independent of wall time.
/// </summary>
internal sealed class FixedClock
{
    private long _ms;

    public FixedClock(long ms) => _ms = ms;

    public long Now() => _ms;

    public void Set(long ms) => _ms = ms;

    public long Add(long delta)
    {
        _ms += delta;
        return _ms;
    }
}

/// <summary>
/// Unit tests for HLC, ported one by one when the server moved from Go to .NET 10;
/// the Go reference used for comparison (legacy-go/) was deleted once migration was verified.
/// </summary>
public class HlcTests
{
    // ── Encoding ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0L, 0, "0000000000000-00000")]
    [InlineData(1000L, 0, "0000000001000-00000")]
    [InlineData(1000L, 42, "0000000001000-00042")]
    [InlineData(1790000000000L, 99999, "1790000000000-99999")]
    [InlineData(9223372036854L, 1, "9223372036854-00001")] // 13-digit cap
    public void Encode_FixedWidthFormat(long l, int c, string want) =>
        Assert.Equal(want, Hlc.Encode(l, c));

    [Fact]
    public void Decode_RoundTripsLosslessly()
    {
        var rnd = new Random(1);
        for (int i = 0; i < 2000; i++)
        {
            long l = (long)rnd.NextInt64(9_000_000_000_000L);
            int c = rnd.Next(100_000);
            string s = Hlc.Encode(l, c);

            Assert.True(Hlc.TryDecode(s, out long gl, out int gc), $"Decode({s}) failed");
            Assert.Equal(l, gl);
            Assert.Equal(c, gc);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("123")]
    [InlineData("0000000001000-0000")]
    [InlineData("0000000001000000000")]
    [InlineData("0000000001000_00000")]
    [InlineData("0000000001000-0000x")]
    [InlineData("abc-00000")]
    [InlineData("0000000001000-999999")] // 6-digit counter
    [InlineData("-0000000001000-00000")]
    public void Decode_RejectsGarbageInput(string s)
    {
        Assert.False(Hlc.TryDecode(s, out _, out _), $"Decode({s}) should fail");
        Assert.False(Hlc.IsValid(s), $"IsValid({s}) should be false");
    }

    [Theory]
    // The next two are measured divergences, not hypothetical:
    // .NET int.TryParse default styles accept signs and surrounding whitespace,
    // while Go ParseUint rejects all of them. Without special handling the two builds
    // would judge the same input differently — on the "invalid input" path that most
    // needs strictness.
    [InlineData("+000000001000-00000")]
    [InlineData(" 000000001000-00000")]
    [InlineData("000000001000-0000 ")]
    [InlineData("00000000+1000-00000")]
    public void Decode_RejectsSignsAndWhitespace(string s) =>
        Assert.False(Hlc.TryDecode(s, out _, out _), $"Decode({s}) should fail (Go side rejects it too)");

    /// <summary>
    /// Lexicographic order == time order. The whole point of the encoding: merges can compare
    /// strings directly in any language or data structure, no parsing needed.
    /// </summary>
    [Fact]
    public void StringOrderEqualsTimeOrder()
    {
        var rnd = new Random(2);
        var enc = new List<string>(1000);
        for (int i = 0; i < 1000; i++)
        {
            // Deliberately cluster many samples in one millisecond to force the "same physical part, counter only" path
            enc.Add(Hlc.Encode(1_000_000_000_000L + rnd.Next(5), rnd.Next(100_000)));
        }

        enc.Sort(StringComparer.Ordinal);
        for (int i = 1; i < enc.Count; i++)
        {
            Assert.True(
                Hlc.Compare(enc[i - 1], enc[i]) <= 0,
                $"not monotonic after sort: \"{enc[i - 1]}\"({i - 1}) > \"{enc[i]}\"({i})");
        }
    }

    // ── Monotonicity and reset ────────────────────────────────────────────

    [Fact]
    public void Now_IsStrictlyMonotonic()
    {
        var clk = new FixedClock(1_000_000_000_000L);
        Hlc h = Hlc.NewWithClock(clk.Now);

        string prev = h.Now();
        for (int i = 0; i < 1000; i++)
        {
            string cur = h.Now();
            Assert.True(Hlc.Compare(cur, prev) > 0, $"Now() #{i} did not advance: \"{prev}\" followed by \"{cur}\"");
            prev = cur;
        }
    }

    [Fact]
    public void Now_ResetsCounterOnNewMillisecond()
    {
        var clk = new FixedClock(1000);
        Hlc h = Hlc.NewWithClock(clk.Now);

        Assert.Equal("0000000001000-00000", h.Now());
        Assert.Equal("0000000001000-00001", h.Now());

        clk.Set(2000);
        Assert.Equal("0000000002000-00000", h.Now());
    }

    [Fact]
    public void LogicalCounterOverflowCarriesIntoPhysicalPart()
    {
        var clk = new FixedClock(1000);
        Hlc h = Hlc.NewWithClock(clk.Now);
        h.SetStateForTest(1000, 99_999 - 2);

        string[] seq = [h.Now(), h.Now(), h.Now(), h.Now(), h.Now()];
        for (int i = 1; i < seq.Length; i++)
        {
            Assert.True(Hlc.Compare(seq[i - 1], seq[i]) <= 0, $"not monotonic near overflow: \"{seq[i - 1]}\" → \"{seq[i]}\"");
            Assert.Equal(19, seq[i].Length);
        }

        Assert.StartsWith("0000000001001-", seq[^1], StringComparison.Ordinal);
    }

    // ── Causality: the entire reason HLC exists ─────────────────────────
    //
    // docs/architecture.md §5: if these tests fail, the original problem — "clock skew between
    // two machines silently drops bookmarks" — is back.

    [Fact]
    public void CausalityAcrossDevices()
    {
        Hlc a = Hlc.NewWithClock(new FixedClock(1_000_000_000_000L).Now);
        Hlc b = Hlc.NewWithClock(new FixedClock(1_000_000_000_000L).Now);

        string last = string.Empty;
        for (int i = 0; i < 5; i++)
        {
            last = a.Now();
        }

        for (int i = 0; i < 5; i++)
        {
            b.Update(last);
            string got = b.Now();
            Assert.True(
                Hlc.Compare(got, last) > 0,
                $"B follow-up event #{i} \"{got}\" not greater than A \"{last}\" — causality violated");
        }
    }

    /// <summary>
    /// The branch this project once got wrong and only caught while writing tests.
    /// </summary>
    /// <remarks>
    /// Scenario: A produces 6 events within ms 1000, the last being (1000, 00005).
    /// Receiver B's physical clock is also exactly 1000ms, with its own logical counter at 0.
    /// <para>
    /// The wrong form (Update's third branch as "max == p then c = 0") gives B
    /// (1000, 0), and its later Now() yields (1000, 1) &lt; (1000, 5) — causality broken,
    /// with "bookmarks randomly lost" as the near-untraceable symptom.
    /// </para>
    /// </remarks>
    [Fact]
    public void Causality_CatchesUpRemoteWithinSameMillisecond()
    {
        Hlc a = Hlc.NewWithClock(new FixedClock(1000).Now);
        Hlc b = Hlc.NewWithClock(new FixedClock(1000).Now);

        string remote = string.Empty;
        for (int i = 0; i < 6; i++)
        {
            remote = a.Now();
        }

        Assert.Equal("0000000001000-00005", remote);

        b.Update(remote);
        string got = b.Now();

        // Key property: B's follow-up event must be strictly greater than what it just received
        Assert.True(
            Hlc.Compare(got, remote) > 0,
            $"B follow-up event \"{got}\" not greater than remote \"{remote}\" — causality broken in catch-up");

        // Exact value: Update itself is a receive event consuming counter 6 (rc+1),
        // the following Now() adds one more to reach 7. Spending one more than 6 is correct —
        // the paper's algorithm counts receiveEvent as an event.
        Assert.Equal("0000000001000-00007", got);
    }

    [Fact]
    public void Causality_IndependentOfClockSkew()
    {
        // B's physical clock runs 1 hour ahead of A. That skew must never affect causality.
        Hlc a = Hlc.NewWithClock(new FixedClock(1_000_000_000_000L).Now);
        Hlc b = Hlc.NewWithClock(new FixedClock(1_000_000_000_000L + 3_600_000L).Now);

        string t1 = a.Now();
        b.Update(t1);
        string t2 = b.Now();
        Assert.True(Hlc.Compare(t2, t1) > 0, $"B event \"{t2}\" not greater than A event \"{t1}\"");

        a.Update(t2);
        string t3 = a.Now();
        Assert.True(Hlc.Compare(t3, t2) > 0, $"A event \"{t3}\" not greater than B event \"{t2}\"");
    }

    /// <summary>Two-way multi-round exchange; every round must strictly advance.</summary>
    [Fact]
    public void Causality_BidirectionalPingPongFiftyRounds()
    {
        Hlc a = Hlc.NewWithClock(new FixedClock(1_000_000_000_000L).Now);
        Hlc b = Hlc.NewWithClock(new FixedClock(1_000_000_000_000L).Now);

        for (int round = 0; round < 50; round++)
        {
            string lastA = a.Now();
            b.Update(lastA);
            string lastB = b.Now();
            Assert.True(Hlc.Compare(lastB, lastA) > 0, $"round {round}: B \"{lastB}\" not greater than A \"{lastA}\"");

            a.Update(lastB);
            string back = a.Now();
            Assert.True(Hlc.Compare(back, lastB) > 0, $"round {round}: A \"{back}\" not greater than B \"{lastB}\"");
        }
    }

    /// <summary>
    /// When the physical clock jumps back, HLC logical time must keep moving forward,
    /// or issued timestamps would collide with newly generated ones.
    /// </summary>
    [Fact]
    public void ClockRollbackDoesNotGoBackward()
    {
        var clk = new FixedClock(2_000_000_000_000L);
        Hlc h = Hlc.NewWithClock(clk.Now);

        string first = h.Now();
        clk.Set(2_000_000_000_000L - 3_600_000L); // User or NTP turned the clock back 1 hour

        string prev = first;
        for (int i = 0; i < 100; i++)
        {
            string cur = h.Now();
            Assert.True(Hlc.Compare(cur, prev) > 0, $"Now() #{i} after rollback did not advance: \"{prev}\" → \"{cur}\"");
            prev = cur;
        }

        Assert.True(Hlc.Compare(h.Current(), first) >= 0, $"post-rollback current \"{h.Current()}\" older than pre-rollback \"{first}\"");
    }

    // ── Comparison and helpers ────────────────────────────────────────────

    [Fact]
    public void Compare_IsTotalOrder()
    {
        string smaller = Hlc.Encode(1000, 5);
        string larger = Hlc.Encode(1000, 6);
        string nextMs = Hlc.Encode(1001, 0);

        Assert.Equal(-1, Hlc.Compare(smaller, larger));
        Assert.Equal(1, Hlc.Compare(larger, smaller));
        Assert.Equal(0, Hlc.Compare(smaller, smaller));
        Assert.Equal(1, Hlc.Compare(nextMs, larger));

        // Invalid values sort before valid ones, so a tainted side always loses and never taints authoritative state
        Assert.Equal(-1, Hlc.Compare("garbage", smaller));
        Assert.Equal(1, Hlc.Compare(smaller, "garbage"));

        // Two invalid sides fall back to ordinal order, keeping determinism
        Assert.Equal(string.CompareOrdinal("a", "b"), Hlc.Compare("a", "b"));
        Assert.Equal(string.CompareOrdinal("b", "a"), Hlc.Compare("b", "a"));
    }

    [Fact]
    public void MaxHlc_TakesMaxAndIgnoresInvalid()
    {
        Assert.Equal(Hlc.Zero, Hlc.MaxHlc([]));

        string a = Hlc.Encode(1000, 1);
        string b = Hlc.Encode(2000, 0);
        string c = Hlc.Encode(999, 99);

        Assert.Equal(b, Hlc.MaxHlc(a, b, c));
        Assert.Equal(a, Hlc.MaxHlc("garbage", a));
    }

    [Fact]
    public void State_MaxHlc_ScansBothMAndA()
    {
        State s = State.New();
        Assert.Equal(Hlc.Zero, s.MaxHlc());

        s.Items["k1"] = new Item { M = Hlc.Encode(1000, 0), A = Hlc.Encode(500, 0) };
        s.Items["k2"] = new Item { M = Hlc.Encode(900, 0), A = Hlc.Encode(2000, 3) };

        // Expect the maximum of the a fields
        Assert.Equal(Hlc.Encode(2000, 3), s.MaxHlc());
    }

    [Fact]
    public void ObserveMany_AbsorbsRemoteMaximum()
    {
        Hlc h = Hlc.NewWithClock(new FixedClock(500).Now);
        string remote = Hlc.Encode(9000, 12);

        string got = h.ObserveMany([Hlc.Encode(100, 0), remote, "garbage"]);
        Assert.True(Hlc.Compare(got, remote) >= 0, $"ObserveMany left \"{got}\" behind remote \"{remote}\"");

        string next = h.Now();
        Assert.True(Hlc.Compare(next, remote) > 0, $"Now() after ObserveMany = \"{next}\" not greater than remote \"{remote}\"");
    }

    [Fact]
    public void Update_InvalidRemoteDoesNotCrashAndStaysMonotonic()
    {
        Hlc h = Hlc.NewWithClock(new FixedClock(1000).Now);

        string first = h.Now();
        string got = h.Update("not an HLC at all");

        Assert.True(Hlc.IsValid(got), $"invalid remote must still yield a valid HLC, got \"{got}\"");
        Assert.True(Hlc.Compare(got, first) > 0, $"after invalid remote \"{got}\" not greater than \"{first}\"");
    }

    /// <summary>HLC is touched from multiple threads (HTTP handlers + snapshots) and must be race-free.</summary>
    [Fact]
    public void IsThreadSafe()
    {
        Hlc h = Hlc.New();
        long baseMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Parallel.For(0, 50, i =>
        {
            for (int j = 0; j < 200; j++)
            {
                switch (i % 3)
                {
                    case 0:
                        _ = h.Now();
                        break;
                    case 1:
                        _ = h.Update(Hlc.Encode(baseMs + j, j));
                        break;
                    default:
                        _ = h.ObserveMany([Hlc.Encode(j, 0)]);
                        break;
                }
            }
        });
    }
}

// ── Cross-client consistency vectors ────────────────────────────────────

internal sealed class HlcVector
{
    [JsonPropertyName("op")]
    public string Op { get; set; } = string.Empty;

    [JsonPropertyName("remote")]
    public string Remote { get; set; } = string.Empty;

    [JsonPropertyName("at")]
    public long At { get; set; }

    [JsonPropertyName("out")]
    public string Out { get; set; } = string.Empty;
}

internal sealed class HlcVectorFile
{
    [JsonPropertyName("vectors")]
    public List<HlcVector> Vectors { get; set; } = [];
}

/// <summary>
/// Cross-client consistency: Go, C#, and JS all answer for the same test/hlc_vectors.json.
/// </summary>
/// <remarks>
/// The two highest-value tests of the whole port:
/// <list type="bullet">
/// <item><b>C# replay</b>: proves the C# implementation matches every Go-produced vector.</item>
/// <item><b>C# export</b>: produces fresh vectors for the JS side to replay, closing the loop.</item>
/// </list>
/// Divergent HLCs make the merge nondeterministic on the "m exactly equal" branch —
/// the same bookmark pair alternately wins across rounds and the two sides diverge.
/// </remarks>
public class HlcVectorTests
{
    /// <summary>
    /// Locate the vectors file: walk up from the test run directory until
    /// <c>test/hlc_vectors.json</c> is found.
    /// </summary>
    /// <remarks>
    /// Deliberately <b>not a fixed number of levels</b>. A fixed depth ("go up 5") breaks on any
    /// directory reshuffle with "file not found" — and if someone then reaches for Skip, the result
    /// is a permanently green suite that never verifies cross-client consistency (exactly the
    /// "green on both sides, yet never verifying" trap). Walk up to the filesystem root; fail if not found.
    /// <para>
    /// No plain relative path either: the test run directory is bin/Debug/net10.0/, while "dotnet test
    /// from the project directory" uses another working directory — only the assembly location works for both.
    /// </para>
    /// </remarks>
    private static string VectorsPath
    {
        get
        {
            for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "test", "hlc_vectors.json");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            // When missing, return an "impossible" path so Assert.True(File.Exists(...))
            // fails with a readable path. Never paper over it with Skip.
            return Path.Combine(
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "test", "hlc_vectors.json")));
        }
    }

    private static List<HlcVector> LoadVectors()
    {
        string path = VectorsPath;
        Assert.True(File.Exists(path), $"vectors file not found at {path} — cross-client verification impossible");
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize(json, TestJsonContext.Default.HlcVectorFile)!.Vectors;
    }

    /// <summary>
    /// Replay every Go-generated vector and verify the actual values.
    /// </summary>
    /// <remarks>
    /// This is a <b>self-check</b>: the replayer only does what the vector says (now → Now(),
    /// update → Update()), then asserts the outcome matches the vector.
    /// <para>
    /// It exists to catch "generator and replayer disagree" bugs: the vectors file is both artifact
    /// and expectation, so if only the generating test verified itself, a generator bug
    /// (e.g. one unconditional extra Now()) would be baked into the file forever, undiscovered.
    /// </para>
    /// </remarks>
    [Fact]
    public void ReplaysVectors()
    {
        List<HlcVector> vectors = LoadVectors();
        Assert.True(vectors.Count >= 15, $"only {vectors.Count} vectors, too few for cross-client coverage");

        var clk = new FixedClock(0);
        Hlc h = Hlc.NewWithClock(clk.Now);

        int bad = 0;
        for (int i = 0; i < vectors.Count; i++)
        {
            HlcVector v = vectors[i];
            clk.Set(v.At);
            string got = v.Op == "update" ? h.Update(v.Remote) : h.Now();

            if (got != v.Out)
            {
                bad++;
                Assert.Fail(
                    $"vector #{i + 1} mismatch (op={v.Op} at={v.At} remote=\"{v.Remote}\"):\n" +
                    $"  vectors file: {v.Out}\n  actual: {got}");
            }
        }

        Assert.True(bad == 0, $"{bad}/{vectors.Count} vectors mismatch — C# has diverged from Go");
    }

    /// <summary>
    /// Regenerate vectors from scratch in C# and compare with the file.
    /// </summary>
    /// <remarks>
    /// Mirrors <see cref="ReplaysVectors"/>: replay proves "C# reproduces the file's
    /// outputs"; this proves "C# recomputed from scratch agrees too" — catching the blind spot
    /// where "replayer and implementation share one bug, so both err together".
    /// <para>
    /// Never overwrites the file: vectors are a three-way shared artifact updated only by the
    /// generator (see <see cref="ExportsVectorsForJsReplay"/> below). Compare only.
    /// </para>
    /// </remarks>
    [Fact]
    public void FreshComputationMatchesVectors()
    {
        List<HlcVector> vectors = LoadVectors();
        var clk = new FixedClock(1_700_000_000_000L);
        Hlc h = Hlc.NewWithClock(clk.Now);

        var produced = new List<string>();
        foreach (HlcVector v in vectors)
        {
            clk.Set(v.At);
            produced.Add(v.Op == "update" ? h.Update(v.Remote) : h.Now());
        }

        var mismatches = new List<string>();
        for (int i = 0; i < vectors.Count; i++)
        {
            if (produced[i] != vectors[i].Out)
            {
                mismatches.Add($"  vector #{i + 1}: file={vectors[i].Out} C#={produced[i]}");
            }
        }

        Assert.True(
            mismatches.Count == 0,
            $"C# recomputation differs from vectors in {mismatches.Count} places:\n{string.Join('\n', mismatches)}");
    }

    /// <summary>
    /// Export vectors for the JS side to replay (equivalent of Go's TestHLCExportVectors).
    /// </summary>
    /// <remarks>
    /// By default the file is **not** overwritten — overwriting is explicit, for the reasons in
    /// <see cref="HlcVectorExportTests.WriteVectors_RefusesWhenDirectoryMissing"/>
    /// (same guard as the Go side). Only writes with <c>BMSYNC_WRITE_VECTORS=1</c> set.
    /// </remarks>
    [Fact]
    public void ExportsVectorsForJsReplay()
    {
        string path = VectorsPath;
        (string blob, int count) = HlcVectorExportTests.BuildVectors();

        if (Environment.GetEnvironmentVariable("BMSYNC_WRITE_VECTORS") != "1")
        {
            // Even without writing, ensure vectors "can be produced": a broken generator must fail
            // at test time, not when someone finally runs the export command.
            Assert.True(count >= 15, $"only produced {count} vectors, insufficient coverage");
            Assert.False(string.IsNullOrWhiteSpace(blob));
            _ = path;
            return;
        }

        string dir = Path.GetDirectoryName(path)!;
        Assert.True(Directory.Exists(dir), $"vectors directory {dir} missing — refuse to create it (a wrong-path signal)");
        File.WriteAllText(path, blob);
    }
}
