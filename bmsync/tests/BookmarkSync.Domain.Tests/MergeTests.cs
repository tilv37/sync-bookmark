namespace BookmarkSync.Domain.Tests;

/// <summary>
/// Unit tests for the merge algorithm, mirroring the merge-algorithm test cases
/// one by one (ported when the server moved from Go to .NET 10).
/// </summary>
/// <remarks>
/// This is the most important test file in the project: merge bugs show as <b>silently lost
/// bookmarks the user never notices</b>, so beyond branch-by-branch coverage there is a final randomized convergence test.
/// </remarks>
public class MergeTests
{
    private static readonly string K = Fixtures.KeyOf("b");

    // ── Basic merge ─────────────────────────────────────────────────────

    [Fact]
    public void BothSidesEmpty()
    {
        MergeResult r = Merger.Merge(State.New(), State.New(), null, Fixtures.DeviceA, Fixtures.T0);

        Assert.Empty(r.State.Items);
        Assert.Equal("created=0 updated=0 deleted=0 unchanged=0", Fixtures.Summarize(r.Summary));
    }

    [Fact]
    public void ServerEmptyClientHasData()
    {
        // Work machine syncing first: local bookmarks, empty cloud
        State incoming = Fixtures.StateOf(
            ("f1", Fixtures.Folder(RootFolders.Toolbar, "Work", 100, 0)),
            ("b1", Fixtures.Bookmark(Fixtures.KeyOf("f1"), "Example", "https://example.com", 100, 1)));

        MergeResult r = Merger.Merge(State.New(), incoming, null, Fixtures.DeviceB, Fixtures.T0);

        Assert.Equal(2, r.State.Items.Count);
        Assert.Equal(2, r.Summary.Created);
        Assert.Empty(r.Conflicts);
    }

    [Fact]
    public void ClientEmptyServerKeepsAll()
    {
        // Cloud has data, another device has nothing local (e.g. fresh profile)
        State server = Fixtures.StateOf(
            ("b1", Fixtures.Bookmark(RootFolders.Unfiled, "Example", "https://example.com", 100, 0)));

        MergeResult r = Merger.Merge(server, State.New(), null, Fixtures.DeviceB, Fixtures.T0);

        Assert.Single(r.State.Items);
        Assert.Equal(1, r.Summary.Unchanged);
        Assert.Equal(0, r.Summary.Created);
    }

    [Fact]
    public void DisjointSidesMerge()
    {
        State server = Fixtures.StateOf(
            ("a", Fixtures.Bookmark(RootFolders.Toolbar, "A", "https://a.example", 100, 0)));
        State incoming = Fixtures.StateOf(
            ("b", Fixtures.Bookmark(RootFolders.Toolbar, "B", "https://b.example", 100, 0)));

        MergeResult r = Merger.Merge(server, incoming, null, Fixtures.DeviceB, Fixtures.T0);

        Assert.Equal(2, r.State.Items.Count);
        Assert.Equal(1, r.Summary.Created);
        Assert.Equal(1, r.Summary.Unchanged);
    }

    [Fact]
    public void Lww_NewerWins()
    {
        Item older = Fixtures.Bookmark(RootFolders.Toolbar, "Old title", "https://example.com", 100, 0);
        Item newer = Fixtures.Bookmark(RootFolders.Toolbar, "New title", "https://example.com", 200, 0);

        State s = State.New();
        s.Items[K] = older;
        State i = State.New();
        i.Items[K] = newer;

        MergeResult r = Merger.Merge(s, i, null, Fixtures.DeviceB, Fixtures.T0);
        Assert.Equal("New title", r.State.Items[K].N);
        Assert.Equal(1, r.Summary.Updated);
    }

    [Fact]
    public void Lww_NewerServerSideWins()
    {
        Item older = Fixtures.Bookmark(RootFolders.Toolbar, "Old title", "https://example.com", 100, 0);
        Item newer = Fixtures.Bookmark(RootFolders.Toolbar, "New title", "https://example.com", 200, 0);

        State s = State.New();
        s.Items[K] = newer;
        State i = State.New();
        i.Items[K] = older;

        MergeResult r = Merger.Merge(s, i, null, Fixtures.DeviceB, Fixtures.T0);
        Assert.Equal("New title", r.State.Items[K].N);
    }

    [Fact]
    public void SameTimestampSameContentKept()
    {
        Item it = Fixtures.Bookmark(RootFolders.Toolbar, "Title", "https://example.com", 100, 3);
        State s = State.New();
        s.Items[K] = it;
        State i = State.New();
        i.Items[K] = it;

        MergeResult r = Merger.Merge(s, i, null, Fixtures.DeviceB, Fixtures.T0);

        Assert.Equal(it, r.State.Items[K]);
        Assert.Empty(r.Conflicts);
        Assert.Equal(1, r.Summary.Unchanged);
    }

    [Fact]
    public void SameTimestampDifferentContentIsDeterministic()
    {
        State s = State.New();
        State i = State.New();
        s.Items[K] = Fixtures.Bookmark(RootFolders.Toolbar, "AAA", "https://example.com", 100, 3);
        i.Items[K] = Fixtures.Bookmark(RootFolders.Toolbar, "BBB", "https://example.com", 100, 3);

        string? first = null;
        for (int run = 0; run < 50; run++)
        {
            MergeResult r = Merger.Merge(s, i, null, Fixtures.DeviceB, Fixtures.T0);
            first ??= r.State.Items[K].N;
            Assert.Equal(first, r.State.Items[K].N);
        }

        MergeResult r0 = Merger.Merge(s, i, null, Fixtures.DeviceB, Fixtures.T0);
        Assert.Single(r0.Conflicts);
    }

    // ── Deletes and tombstones ────────────────────────────────────────────

    [Fact]
    public void DeletePropagates()
    {
        Item alive = Fixtures.Bookmark(RootFolders.Toolbar, "Title", "https://example.com", 100, 0);
        Item dead = Fixtures.Tomb(alive, Fixtures.T0, 200, 0);

        State s = State.New();
        State i = State.New();
        s.Items[K] = alive;
        i.Items[K] = dead;

        MergeResult r = Merger.Merge(s, i, null, Fixtures.DeviceB, Fixtures.T0);

        Assert.True(r.State.Items[K].D, "newer client delete did not propagate to server state");
        Assert.Equal(1, r.Summary.Deleted);
    }

    [Fact]
    public void OlderDeleteLosesToNewerEdit()
    {
        // Device A deleted the bookmark, device B edited it afterwards (still in use)
        Item dead = Fixtures.Tomb(
            Fixtures.Bookmark(RootFolders.Toolbar, "Title", "https://example.com", 100, 0), Fixtures.T0, 100, 0);
        Item revived = Fixtures.Bookmark(RootFolders.Toolbar, "Title", "https://example.com", 200, 0);

        State s = State.New();
        State i = State.New();
        s.Items[K] = dead;
        i.Items[K] = revived;

        MergeResult r = Merger.Merge(s, i, null, Fixtures.DeviceB, Fixtures.T0);
        Assert.False(r.State.Items[K].D, "older delete must not overwrite newer edit");
    }

    [Fact]
    public void BothSidesTombstones()
    {
        Item alive = Fixtures.Bookmark(RootFolders.Toolbar, "Title", "https://example.com", 100, 0);
        State s = State.New();
        State i = State.New();
        s.Items[K] = Fixtures.Tomb(alive, Fixtures.T0, 200, 0);
        i.Items[K] = Fixtures.Tomb(alive, Fixtures.T0, 100, 0); // older delete

        MergeResult r = Merger.Merge(s, i, null, Fixtures.DeviceB, Fixtures.T0);
        Item got = r.State.Items[K];

        Assert.True(got.D, "both sides tombstoned, result must be a tombstone");
        Assert.Equal(Hlc.Encode(200, 0), got.M);
        // x keeps the earlier one: GC decides by x, earlier reaping saves more space
        Assert.Equal(Fixtures.T0, got.X);
    }

    [Fact]
    public void NewerReaddResurrects()
    {
        // Tombstone + client re-adds the same URL with a newer timestamp -> resurrected
        Item alive = Fixtures.Bookmark(RootFolders.Toolbar, "Title", "https://example.com", 100, 0);

        State s = State.New();
        State i = State.New();
        s.Items[K] = Fixtures.Tomb(alive, Fixtures.T0, 200, 0);
        i.Items[K] = alive with { M = Hlc.Encode(300, 0), D = false, X = 0 };

        MergeResult r = Merger.Merge(s, i, null, Fixtures.DeviceB, Fixtures.T0);

        Assert.False(r.State.Items[K].D, "newer re-add should resurrect the bookmark");
        Assert.Equal("Title", r.State.Items[K].N);
    }

    // ── Idempotency and convergence ────────────────────────────────────────

    /// <summary>
    /// Syncing the same request repeatedly must produce identical results.
    /// This is what keeps "the user hammering sync three times" from corrupting bookmarks.
    /// </summary>
    [Fact]
    public void IsIdempotent()
    {
        State server = Fixtures.StateOf(
            ("f", Fixtures.Folder(RootFolders.Toolbar, "Folder", 100, 0)),
            ("b1", Fixtures.Bookmark(Fixtures.KeyOf("f"), "One", "https://one.example", 100, 1)),
            ("b2", Fixtures.Tomb(
                Fixtures.Bookmark(RootFolders.Toolbar, "Two", "https://two.example", 100, 0), Fixtures.T0, 100, 2)));
        State incoming = Fixtures.StateOf(
            ("f", Fixtures.Folder(RootFolders.Toolbar, "Folder", 100, 0)),
            ("b1", Fixtures.Bookmark(Fixtures.KeyOf("f"), "One edited", "https://one.example", 200, 0)),
            ("b3", Fixtures.Bookmark(RootFolders.Menu, "Three", "https://three.example", 200, 1)),
            ("b2", Fixtures.Tomb(
                Fixtures.Bookmark(RootFolders.Toolbar, "Two", "https://two.example", 100, 0), Fixtures.T0, 100, 2)));

        State once = Merger.Merge(server, incoming, null, Fixtures.DeviceB, Fixtures.T0).State;
        for (int n = 0; n < 5; n++)
        {
            State again = Merger.Merge(server, incoming, null, Fixtures.DeviceB, Fixtures.T0).State;
            Assert.True(Fixtures.SameStates(once, again), $"repeat sync #{n + 1} diverged");
        }

        // Merging again with the result as server and the same request must be a no-op
        State twice = Merger.Merge(once, incoming, null, Fixtures.DeviceB, Fixtures.T0).State;
        Assert.True(Fixtures.SameStates(once, twice), "re-merging with the merged result as server changed the outcome");
    }

    /// <summary>Out-of-order syncs must converge: whatever order A and B arrive in, the authoritative server state is the same.</summary>
    [Fact]
    public void OutOfOrderConverges()
    {
        State b = Fixtures.StateOf(
            ("f", Fixtures.Folder(RootFolders.Toolbar, "Folder", 100, 0)),
            ("b1", Fixtures.Bookmark(Fixtures.KeyOf("f"), "One", "https://one.example", 100, 1)),
            ("b2", Fixtures.Bookmark(RootFolders.Unfiled, "Two", "https://two.example", 100, 2)));
        State patchA = Fixtures.StateOf(
            ("b1", Fixtures.Bookmark(Fixtures.KeyOf("f"), "One edited", "https://one.example", 300, 0)),
            ("b3", Fixtures.Bookmark(RootFolders.Unfiled, "Three", "https://three.example", 250, 0)));
        State patchB = Fixtures.StateOf(
            ("b2", Fixtures.Bookmark(RootFolders.Unfiled, "Two edited", "https://two.example", 400, 0)));

        State ab = Merger.Merge(
            Merger.Merge(b, patchA, null, Fixtures.DeviceA, Fixtures.T0).State,
            patchB, null, Fixtures.DeviceB, Fixtures.T0).State;
        State ba = Merger.Merge(
            Merger.Merge(b, patchB, null, Fixtures.DeviceB, Fixtures.T0).State,
            patchA, null, Fixtures.DeviceA, Fixtures.T0).State;

        Assert.True(
            Fixtures.SameStates(ab, ba),
            $"A-then-B vs B-then-A diverged:\n AB: {Describe(ab)}\n BA: {Describe(ba)}");

        Assert.Equal(4, ab.Items.Count);
        Assert.Equal("Two edited", ab.Items[Fixtures.KeyOf("b2")].N);
        Assert.Equal("Three", ab.Items[Fixtures.KeyOf("b3")].N);
    }

    private static string Describe(State s) =>
        string.Join(", ", s.KeysSorted().Select(k => $"{k[..4]}={s.Items[k].N}({s.Items[k].M})"));

    // ── Conflict detection ──────────────────────────────────────────────

    /// <summary>
    /// A first sync (empty base) must never report conflicts, or the user's first sync
    /// would drown in conflict records and the feature would be useless.
    /// </summary>
    [Fact]
    public void NoConflictWhenBaseEmpty()
    {
        State server = Fixtures.StateOf(
            ("b", Fixtures.Bookmark(RootFolders.Toolbar, "Cloud title", "https://example.com", 200, 0)));
        State incoming = Fixtures.StateOf(
            ("b", Fixtures.Bookmark(RootFolders.Toolbar, "Local title", "https://example.com", 100, 0)));

        MergeResult r = Merger.Merge(server, incoming, null, Fixtures.DeviceB, Fixtures.T0);

        Assert.Empty(r.Conflicts);
        Assert.Equal("Cloud title", r.State.Items[K].N);
    }

    [Fact]
    public void SingleSidedEditIsNotConflict()
    {
        Item orig = Fixtures.Bookmark(RootFolders.Toolbar, "Original title", "https://example.com", 100, 0);
        var baseMap = new Dictionary<string, string>(StringComparer.Ordinal) { [K] = orig.M };

        // Only the client changed
        MergeResult r1 = Merger.Merge(
            Fixtures.StateOf(("b", orig)),
            Fixtures.StateOf(("b", Fixtures.Bookmark(RootFolders.Toolbar, "New title", "https://example.com", 300, 0))),
            baseMap, Fixtures.DeviceB, Fixtures.T0);
        Assert.Empty(r1.Conflicts);

        // Only the server changed
        MergeResult r2 = Merger.Merge(
            Fixtures.StateOf(("b", Fixtures.Bookmark(RootFolders.Toolbar, "Cloud edit", "https://example.com", 300, 0))),
            Fixtures.StateOf(("b", orig)),
            baseMap, Fixtures.DeviceB, Fixtures.T0);
        Assert.Empty(r2.Conflicts);
    }

    [Fact]
    public void DetectsTrueConcurrentEdit()
    {
        Item orig = Fixtures.Bookmark(RootFolders.Toolbar, "Original title", "https://example.com", 100, 0);
        var baseMap = new Dictionary<string, string>(StringComparer.Ordinal) { [K] = orig.M };

        State server = Fixtures.StateOf(
            ("b", Fixtures.Bookmark(RootFolders.Toolbar, "Cloud edit", "https://example.com", 300, 0)));
        State incoming = Fixtures.StateOf(
            ("b", Fixtures.Bookmark(RootFolders.Toolbar, "Local edit", "https://example.com", 200, 0)));

        MergeResult r = Merger.Merge(server, incoming, baseMap, Fixtures.DeviceB, Fixtures.T0);

        Conflict c = Assert.Single(r.Conflicts);
        Assert.Equal(ConflictReasons.ConcurrentEdit, c.Reason);
        Assert.Equal("title", c.Field);
        Assert.Equal(ConflictWinners.Server, c.Winner);
        Assert.Equal(Fixtures.DeviceB, c.Loser);
        Assert.Equal("Cloud edit", c.WinnerValue);
        Assert.Equal("Local edit", c.LoserValue);
        Assert.Equal("https://example.com", c.Url);

        // Conflicts are purely observational: the winner still follows LWW
        Assert.Equal("Cloud edit", r.State.Items[K].N);
    }

    public static TheoryData<string, Item, Item, string> ConflictFieldCases()
    {
        Item orig = Fixtures.Bookmark(RootFolders.Toolbar, "Title", "https://example.com", 100, 0);

        var data = new TheoryData<string, Item, Item, string>
        {
            { "Title changed", orig, Fixtures.Bookmark(RootFolders.Toolbar, "New", "https://example.com", 200, 0), "title" },
            { "URL changed", orig, Fixtures.Bookmark(RootFolders.Toolbar, "Title", "https://other.example", 200, 0), "url" },
            { "Parent changed", orig, Fixtures.Bookmark(RootFolders.Menu, "Title", "https://example.com", 200, 0), "parent" },
            // Bookmark-to-folder: same P, same N, only T differs — FirstDifferingField takes
            // the first difference in fixed parent→type→title→url order, so "type"
            { "Bookmark became folder", orig, Fixtures.Folder(RootFolders.Toolbar, "Title", 200, 0), "type" },
            // P and T differ together: the fixed order always reports "parent", stable across rounds
            { "Parent and type both changed", orig, Fixtures.Folder(RootFolders.Menu, "Title", 200, 0), "parent" },
        };

        return data;
    }

    [Theory]
    [MemberData(nameof(ConflictFieldCases))]
    public void ConflictRecordsCorrectField(string name, Item serverItem, Item clientItem, string wantField)
    {
        Item orig = Fixtures.Bookmark(RootFolders.Toolbar, "Title", "https://example.com", 100, 0);
        var baseMap = new Dictionary<string, string>(StringComparer.Ordinal) { [K] = orig.M };

        State s = State.New();
        State i = State.New();
        // Both sides "changed": the server baseline must also be newer than base
        s.Items[K] = serverItem with { M = Hlc.Encode(300, 0) };
        i.Items[K] = clientItem;

        MergeResult r = Merger.Merge(s, i, baseMap, Fixtures.DeviceB, Fixtures.T0);

        Conflict c = Assert.Single(r.Conflicts);
        Assert.Equal(wantField, c.Field);
        _ = name;
    }

    // ── Invariants ──────────────────────────────────────────────────────

    [Fact]
    public void DoesNotMutateInputs()
    {
        State server = Fixtures.StateOf(
            ("b", Fixtures.Bookmark(RootFolders.Toolbar, "One", "https://one.example", 100, 0)));
        State incoming = Fixtures.StateOf(
            ("b", Fixtures.Bookmark(RootFolders.Toolbar, "Two", "https://one.example", 200, 0)));
        var baseMap = new Dictionary<string, string>(StringComparer.Ordinal) { [K] = Hlc.Encode(50, 0) };

        State serverBefore = server.Clone();
        State incomingBefore = incoming.Clone();
        var baseBefore = new Dictionary<string, string>(baseMap, StringComparer.Ordinal);

        _ = Merger.Merge(server, incoming, baseMap, Fixtures.DeviceB, Fixtures.T0);

        Assert.True(Fixtures.SameStates(server, serverBefore), "Merge mutated input server");
        Assert.True(Fixtures.SameStates(incoming, incomingBefore), "Merge mutated input incoming");
        Assert.Equal(baseBefore.Count, baseMap.Count);
        foreach ((string k, string v) in baseMap)
        {
            Assert.Equal(baseBefore[k], v);
        }
    }

    /// <summary>
    /// A server-side item never vanishes silently — deletes must be explicit tombstones.
    /// This is the direct expression of "merge never silently drops bookmarks".
    /// </summary>
    [Fact]
    public void NeverDropsServerItems()
    {
        State server = Fixtures.StateOf(
            ("a", Fixtures.Bookmark(RootFolders.Toolbar, "One", "https://one.example", 100, 0)),
            ("b", Fixtures.Bookmark(RootFolders.Toolbar, "Two", "https://two.example", 100, 0)),
            ("c", Fixtures.Folder(RootFolders.Unfiled, "Folder", 100, 0)));

        MergeResult r = Merger.Merge(server, State.New(), null, Fixtures.DeviceB, Fixtures.T0);

        foreach (string k in server.KeysSorted())
        {
            Assert.Contains(k, r.State.Items.Keys);
        }
    }

    [Fact]
    public void MergeResultIsSelfConsistent()
    {
        State server = Fixtures.StateOf(
            ("f", Fixtures.Folder(RootFolders.Toolbar, "Folder", 100, 0)),
            ("b", Fixtures.Tomb(
                Fixtures.Bookmark(Fixtures.KeyOf("f"), "One", "https://one.example", 100, 0), Fixtures.T0, 100, 0)));
        State incoming = Fixtures.StateOf(
            ("f", Fixtures.Folder(RootFolders.Toolbar, "Folder", 100, 0)),
            ("b", Fixtures.Bookmark(Fixtures.KeyOf("f"), "One", "https://one.example", 300, 0))); // resurrected

        MergeResult r = Merger.Merge(server, incoming, null, Fixtures.DeviceB, Fixtures.T0);

        ValidationResult v = r.State.Validate();
        Assert.True(v.IsValid, $"merge result failed its own Validate: {v.Error}");
    }

    // ── Randomized property tests ───────────────────────────────────────
    //
    // Hand-written cases only cover "scenarios we thought of". The randomized convergence test
    // is what really catches edge bugs: random op sequences replayed in any order must converge
    // the server to the same state.

    [Fact]
    public void RandomizedConvergence()
    {
        var rnd = new Random(20260928);
        const int devices = 3;

        var truth = new State[devices];
        for (int d = 0; d < devices; d++)
        {
            truth[d] = State.New();
        }

        long clock = 1000;

        for (int op = 0; op < 400; op++)
        {
            clock += rnd.Next(3);
            int d = rnd.Next(devices);
            int action = rnd.Next(4);
            string k = Fixtures.KeyOf($"item{(char)('a' + rnd.Next(20))}");

            switch (action)
            {
                case 0: // Add or overwrite
                    truth[d].Items[k] = Fixtures.Bookmark(
                        RootFolders.Toolbar, "t" + k[..4], "https://e.example/" + k[..4], clock, rnd.Next(50));
                    break;

                case 1: // Delete (tombstone)
                    if (truth[d].Items.TryGetValue(k, out Item prev))
                    {
                        truth[d].Items[k] = Fixtures.Tomb(prev, Fixtures.T0, clock, rnd.Next(50));
                    }

                    break;

                case 2: // Retitle
                    if (truth[d].Items.TryGetValue(k, out Item prev2))
                    {
                        truth[d].Items[k] = prev2 with
                        {
                            N = "changed" + k[..4],
                            M = Hlc.Encode(clock, rnd.Next(50)),
                        };
                    }

                    break;

                case 3:
                    // Drop the local copy (device switched to a fresh profile and reports empty state):
                    // the device would report its cached content; leave truth untouched here
                    break;
            }
        }

        // Merge the three device copies into the server in various orders
        State server = State.New();
        int[] order = [0, 1, 2];
        Shuffle(rnd, order);
        foreach (int d in order)
        {
            server = Merger.Merge(server, truth[d], null, "dev", Fixtures.T0).State;
        }

        // Merge again in another order; the result must not change (convergence)
        State again = State.New();
        foreach (int d in new[] { 2, 0, 1 })
        {
            again = Merger.Merge(again, truth[d], null, "dev", Fixtures.T0).State;
        }

        Assert.True(
            Fixtures.SameStates(server, again),
            $"different merge orders diverged, convergence broken:\n A: {Describe(server)}\n B: {Describe(again)}");

        ValidationResult v = server.Validate();
        Assert.True(v.IsValid, $"randomized merge result failed Validate: {v.Error}");

        // Key point: the server result must contain **every** key any device reported
        var allKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (State t in truth)
        {
            foreach (string k in t.Items.Keys)
            {
                allKeys.Add(k);
            }
        }

        foreach (string k in allKeys)
        {
            Assert.Contains(k, server.Items.Keys);
        }
    }

    private static void Shuffle(Random rnd, int[] values)
    {
        for (int i = values.Length - 1; i > 0; i--)
        {
            int j = rnd.Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }
}
