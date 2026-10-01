namespace BookmarkSync.Domain.Tests;

/// <summary>
/// Unit tests for tombstone GC, mirroring the tombstone-GC test cases.
/// </summary>
/// <remarks>
/// A GC bug silently drops bookmarks, so every case here pins one safety constraint.
/// </remarks>
public class TombstoneGcTests
{
    private static readonly long Day = 24 * 60 * 60 * 1000L;
    private static readonly TimeSpan Ttl90 = TimeSpan.FromDays(90);

    [Fact]
    public void RemovesOnlyExpiredTombstones()
    {
        long now = Fixtures.T0;

        var items = State.NewItemsDictionary();
        // Live items: never touched, however old
        items[Fixtures.KeyOf("live-old")] = Fixtures.Bookmark(RootFolders.Toolbar, "Added long ago", "https://a.example", 1, 0);
        items[Fixtures.KeyOf("live-new")] = Fixtures.Bookmark(RootFolders.Toolbar, "Just added", "https://b.example", 999_999, 0);
        // Tombstone from 91 days ago → should be reaped
        items[Fixtures.KeyOf("dead-91d")] =
            Fixtures.Tomb(Fixtures.Bookmark(RootFolders.Toolbar, "x", "https://c.example", 100, 0), now - 91 * Day, 100, 0);
        // Tombstone from 89 days ago → kept
        items[Fixtures.KeyOf("dead-89d")] =
            Fixtures.Tomb(Fixtures.Bookmark(RootFolders.Toolbar, "y", "https://d.example", 100, 0), now - 89 * Day, 100, 0);
        // Freshly deleted tombstone → kept
        items[Fixtures.KeyOf("dead-today")] =
            Fixtures.Tomb(Fixtures.Bookmark(RootFolders.Toolbar, "z", "https://e.example", 100, 0), now - 1 * Day, 100, 0);

        (Dictionary<string, Item> output, int removed) = TombstoneGc.Collect(items, now, Ttl90);

        Assert.Equal(1, removed);
        Assert.DoesNotContain(Fixtures.KeyOf("dead-91d"), output.Keys);
        foreach (string label in new[] { "dead-89d", "dead-today", "live-old", "live-new" })
        {
            Assert.Contains(Fixtures.KeyOf(label), output.Keys);
        }

        Assert.Equal(4, output.Count);
    }

    [Fact]
    public void NeverTouchesLiveItems()
    {
        var items = State.NewItemsDictionary();
        items[Fixtures.KeyOf("a")] = Fixtures.Bookmark(RootFolders.Toolbar, "a", "https://a.example", 1, 0);
        items[Fixtures.KeyOf("b")] = Fixtures.Folder(RootFolders.Unfiled, "b", 1, 0);
        items[Fixtures.KeyOf("c")] = Fixtures.Folder(RootFolders.Menu, "c", 1, 0);

        // TTL = 0, everything should expire
        (Dictionary<string, Item> output, int removed) = TombstoneGc.Collect(items, Fixtures.T0, TimeSpan.Zero);

        Assert.Equal(0, removed);
        Assert.Equal(3, output.Count);
    }

    /// <summary>
    /// x == 0 means "deletion time unknown". Better to keep a tombstone (wasting a few hundred
    /// bytes) than to drop an "unknown deletion time" fact — that would silently break delete sync.
    /// </summary>
    [Fact]
    public void KeepsTombstonesWithoutDeletionTime()
    {
        Item it = Fixtures.Bookmark(RootFolders.Toolbar, "x", "https://x.example", 100, 0) with { D = true, X = 0 };

        var items = State.NewItemsDictionary();
        items[Fixtures.KeyOf("x")] = it;

        (Dictionary<string, Item> output, int removed) =
            TombstoneGc.Collect(items, Fixtures.T0, TimeSpan.Zero);

        Assert.Equal(0, removed);
        Assert.Contains(Fixtures.KeyOf("x"), output.Keys);
    }

    [Fact]
    public void Boundary_EmptyDictionary()
    {
        (Dictionary<string, Item> output, int removed) =
            TombstoneGc.Collect(State.NewItemsDictionary(), Fixtures.T0, Ttl90);

        Assert.Equal(0, removed);
        Assert.Empty(output);
    }

    [Fact]
    public void Boundary_ExactlyAtTtl()
    {
        long at = Fixtures.T0 - (long)Ttl90.TotalMilliseconds;
        Item it = Fixtures.Tomb(Fixtures.Bookmark(RootFolders.Toolbar, "x", "https://x.example", 1, 0), at, 1, 0);

        // The rule is x < cutoff (strictly less); exactly at the boundary is kept
        var items = State.NewItemsDictionary();
        items[Fixtures.KeyOf("x")] = it;
        Assert.Equal(0, TombstoneGc.Collect(items, Fixtures.T0, Ttl90).Removed);

        // 1ms earlier should be reaped
        items[Fixtures.KeyOf("x")] = it with { X = at - 1 };
        Assert.Equal(1, TombstoneGc.Collect(items, Fixtures.T0, Ttl90).Removed);
    }

    [Fact]
    public void DoesNotMutateInputDictionary()
    {
        var items = State.NewItemsDictionary();
        items[Fixtures.KeyOf("d")] =
            Fixtures.Tomb(Fixtures.Bookmark(RootFolders.Toolbar, "x", "https://x.example", 1, 0), Fixtures.T0 - Day, 1, 0);

        TombstoneGc.Collect(items, Fixtures.T0, Ttl90);

        Assert.Single(items);
    }

    [Fact]
    public void CollectFrom_ReapsAndReturnsNewState()
    {
        State s = Fixtures.StateOf(
            ("live", Fixtures.Bookmark(RootFolders.Toolbar, "Live", "https://a.example", 1, 0)),
            ("dead", Fixtures.Tomb(Fixtures.Bookmark(RootFolders.Toolbar, "Dead", "https://b.example", 1, 0), Fixtures.T0 - 100 * Day, 1, 0)));

        (State after, int removed) = TombstoneGc.CollectFrom(s, Fixtures.T0, Ttl90);

        Assert.Equal(1, removed);
        Assert.Single(after.Items);

        // With nothing to do, return the **same** State object to avoid pointless allocation.
        // Deliberately preserved from the port: Go achieved it via "not replacing the map pointer";
        // here "not returning a new object" does it, and the return value shows it directly.
        (State again, int removed2) = TombstoneGc.CollectFrom(after, Fixtures.T0, Ttl90);
        Assert.Equal(0, removed2);
        Assert.Same(after, again);
    }

    /// <summary>
    /// Full lifecycle: create → sync → delete → sync → GC reap.
    /// Confirms tombstones really vanish after 90 days instead of piling up forever.
    /// </summary>
    [Fact]
    public void TombstoneFullLifecycle()
    {
        string k = Fixtures.KeyOf("b");
        State s = State.New();
        s.Items[k] = Fixtures.Bookmark(RootFolders.Toolbar, "Title", "https://example.com", 100, 0);

        // Deleted 100 days later
        long deleteAt = Fixtures.T0 + 100 * Day;
        s.Items[k] = Fixtures.Tomb(s.Items[k], deleteAt, 200, 0);
        Assert.Equal(0, s.CountActive());

        // 89 days after deletion: still there
        (State s1, int f1) = TombstoneGc.CollectFrom(s, deleteAt + 89 * Day, Ttl90);
        Assert.Equal(0, f1);
        Assert.Single(s1.Items);

        // 91 days after deletion: reaped
        (State s2, int f2) = TombstoneGc.CollectFrom(s1, deleteAt + 91 * Day, Ttl90);
        Assert.Equal(1, f2);
        Assert.Empty(s2.Items);
    }
}
