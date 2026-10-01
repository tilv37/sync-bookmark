namespace BookmarkSync.Domain.Tests;

/// <summary>
/// Unit tests for <see cref="State"/>, mirroring the data-model and validation test cases.
/// </summary>
public class StateTests
{
    [Fact]
    public void Validate_AcceptsWellFormedState()
    {
        State s = Fixtures.StateOf(
            ("f", Fixtures.ValidFolder(RootFolders.Toolbar)),
            ("b", Fixtures.ValidBookmark(Fixtures.KeyOf("f"))),
            ("top", Fixtures.ValidBookmark(RootFolders.Unfiled)));

        Assert.True(s.Validate().IsValid);
    }

    /// <summary>Hard errors: inputs that break parsing or blow up resources must be rejected.</summary>
    public static TheoryData<string, Action<State>, string> HardErrorCases()
    {
        var data = new TheoryData<string, Action<State>, string>();

        data.Add("key too short", s => s.Items["ab"] = Fixtures.ValidBookmark(RootFolders.Toolbar), "invalid item key");
        data.Add(
            "key not hex",
            s =>
            {
                s.Items.Remove(Fixtures.KeyOf("b"));
                s.Items[new string('z', 32)] = Fixtures.ValidBookmark(RootFolders.Toolbar);
            },
            "invalid item key");
        data.Add(
            "invalid type",
            s => s.Items[Fixtures.KeyOf("b")] = Fixtures.ValidBookmark(RootFolders.Toolbar) with { T = "x" },
            "invalid type");
        data.Add(
            "bookmark missing url",
            s => s.Items[Fixtures.KeyOf("b")] = Fixtures.ValidBookmark(RootFolders.Toolbar) with { U = string.Empty },
            "must have url");
        data.Add(
            "folder with url",
            s => s.Items[Fixtures.KeyOf("b")] = Fixtures.ValidFolder(RootFolders.Toolbar) with { U = "https://x.example" },
            "must not have url");
        data.Add(
            "bad m",
            s => s.Items[Fixtures.KeyOf("b")] = Fixtures.ValidBookmark(RootFolders.Toolbar) with { M = "yesterday" },
            "not a valid HLC");
        data.Add(
            "bad a",
            s => s.Items[Fixtures.KeyOf("b")] = Fixtures.ValidBookmark(RootFolders.Toolbar) with { A = "???" },
            "not a valid HLC");
        data.Add(
            "title too long",
            s => s.Items[Fixtures.KeyOf("b")] =
                Fixtures.ValidBookmark(RootFolders.Toolbar) with { N = new string('x', Limits.MaxTitleLen + 1) },
            "title length");
        data.Add(
            "url too long",
            s => s.Items[Fixtures.KeyOf("b")] = Fixtures.ValidBookmark(RootFolders.Toolbar) with
            {
                U = "https://x.example/" + new string('y', Limits.MaxUrlLen),
            },
            "url length");
        data.Add(
            "depth over limit",
            s =>
            {
                var fresh = State.New();
                string parent = RootFolders.Toolbar;
                for (int i = 0; i <= Limits.MaxDepth + 2; i++)
                {
                    string fk = Fixtures.KeyOf($"deep{(char)('a' + (i % 26))}{(char)('a' + (i / 26))}");
                    fresh.Items[fk] = Fixtures.ValidFolder(parent);
                    parent = fk;
                }

                foreach ((string k, Item v) in fresh.Items)
                {
                    s.Items[k] = v;
                }
            },
            "depth");

        return data;
    }

    [Theory]
    [MemberData(nameof(HardErrorCases))]
    public void Validate_HardErrorsAreRejected(string name, Action<State> mutate, string wantSub)
    {
        State s = Fixtures.StateOf(("b", Fixtures.ValidBookmark(RootFolders.Toolbar)));
        mutate(s);

        ValidationResult res = s.Validate();
        Assert.False(res.IsValid, $"should have been rejected but passed: {name}");
        Assert.Contains(wantSub, res.Error!, StringComparison.Ordinal);
    }

    /// <summary>Version mismatch is tested separately: <c>State.V</c> is init-only and cannot be changed via <see cref="Action{T}"/>.</summary>
    [Fact]
    public void Validate_VersionMismatchIsRejected()
    {
        State withBadVersion = new() { V = 99 };
        ValidationResult r = withBadVersion.Validate();

        Assert.False(r.IsValid);
        Assert.Contains("schema version", r.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_OverLimitCountIsRejected()
    {
        // Only build a key set, reusing one valid item value — the goal is pushing the item
        // count over the limit without caring about each item's content.
        State s = State.New();
        for (int i = 0; i <= Limits.MaxItems; i++)
        {
            s.Items[Fixtures.KeyOf($"bulk{i}")] = Fixtures.ValidBookmark(RootFolders.Toolbar);
        }

        ValidationResult r = s.Validate();
        Assert.False(r.IsValid);
        Assert.Contains("exceeds limit", r.Error!, StringComparison.Ordinal);
    }

    /// <summary>Soft warnings: tree-structure problems are recorded, not rejected.</summary>
    /// <remarks>
    /// Rejecting a whole upload over one orphaned item is far worse for users than skipping that item.
    /// </remarks>
    public static TheoryData<string, Action<State>, string> SoftWarningCases()
    {
        var data = new TheoryData<string, Action<State>, string>();

        data.Add(
            "missing parent",
            s => s.Items[Fixtures.KeyOf("b")] =
                s.Items[Fixtures.KeyOf("b")] with { P = Fixtures.KeyOf("ghost") },
            "parent");

        data.Add(
            "parent not a folder",
            s => s.Items[Fixtures.KeyOf("b")] =
                s.Items[Fixtures.KeyOf("b")] with { P = Fixtures.KeyOf("other") },
            "is not a folder");

        data.Add(
            "live item with deleted parent",
            s =>
            {
                s.Items[Fixtures.KeyOf("f")] = Fixtures.Tomb(Fixtures.ValidFolder(RootFolders.Toolbar), Fixtures.T0, 200, 0);
                s.Items[Fixtures.KeyOf("b")] = s.Items[Fixtures.KeyOf("b")] with { P = Fixtures.KeyOf("f") };
            },
            "is deleted");

        data.Add(
            "parent cycle",
            s =>
            {
                string a = Fixtures.KeyOf("cyc-a"), b = Fixtures.KeyOf("cyc-b");
                s.Items[a] = Fixtures.ValidFolder(b);
                s.Items[b] = Fixtures.ValidFolder(a);
            },
            "broken parent chain");

        return data;
    }

    [Theory]
    [MemberData(nameof(SoftWarningCases))]
    public void Validate_StructuralIssuesBecomeWarnings(string name, Action<State> mutate, string wantWarn)
    {
        State s = Fixtures.StateOf(
            ("b", Fixtures.ValidBookmark(RootFolders.Toolbar)),
            ("other", Fixtures.ValidBookmark(RootFolders.Menu)));

        mutate(s);

        ValidationResult r = s.Validate();

        // Keep the name: when an assertion fails it shows which scenario regressed —
        // every case here is "structurally malformed", and warning text alone would not tell them apart.
        Assert.True(r.IsValid, $"[{name}] structural issues should warn, not hard-fail, but got: {r.Error}");

        string joined = string.Join("; ", r.Warnings);
        Assert.Contains(wantWarn, joined, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_TombstoneMissingDeletionTimeWarnsOnly()
    {
        Item it = Fixtures.ValidBookmark(RootFolders.Toolbar) with { D = true, X = 0 };
        State s = Fixtures.StateOf(("b", it));

        ValidationResult r = s.Validate();
        Assert.True(r.IsValid, $"should not be a hard error: {r.Error}");
        Assert.Contains("missing deletion time", string.Join(";", r.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_EmptyStateIsValid()
    {
        Assert.True(State.New().Validate().IsValid);
    }

    [Fact]
    public void Clone_IsDeepCopy()
    {
        State s = Fixtures.StateOf(("b", Fixtures.ValidBookmark(RootFolders.Toolbar)));
        State c = s.Clone();

        c.Items[Fixtures.KeyOf("b")] = Fixtures.ValidFolder(RootFolders.Menu);
        c.Items["new"] = Fixtures.ValidBookmark(RootFolders.Unfiled);

        Assert.Single(s.Items);
        Assert.Equal(ItemTypes.Bookmark, s.Items[Fixtures.KeyOf("b")].T);
    }

    [Fact]
    public void CountActive_ExcludesTombstones()
    {
        State s = Fixtures.StateOf(
            ("a", Fixtures.ValidBookmark(RootFolders.Toolbar)),
            ("b", Fixtures.ValidFolder(RootFolders.Menu)),
            ("c", Fixtures.Tomb(Fixtures.ValidBookmark(RootFolders.Unfiled), Fixtures.T0, 200, 0)));

        Assert.Equal(2, s.CountActive());
    }

    [Fact]
    public void KeysSorted_StableAcrossCalls()
    {
        State s = State.New();
        for (int i = 0; i < 50; i++)
        {
            s.Items[Fixtures.KeyOf($"k{(char)('a' + (i % 26))}{(char)('a' + (i / 26))}")] =
                Fixtures.ValidBookmark(RootFolders.Toolbar);
        }

        string[] first = s.KeysSorted();
        for (int i = 0; i < 20; i++)
        {
            string[] got = s.KeysSorted();
            Assert.Equal(first.Length, got.Length);
            Assert.Equal(first, got);
        }
    }

    [Fact]
    public void RootFolderConstantsAreCorrect()
    {
        foreach (string id in new[]
                 {
                     RootFolders.Toolbar, RootFolders.Menu, RootFolders.Unfiled, RootFolders.Mobile,
                 })
        {
            Assert.True(RootFolders.IsRootFolder(id), $"\"{id}\" should be recognized as a root folder");
        }

        Assert.False(RootFolders.IsRootFolder("some-folder-key"));
        Assert.False(RootFolders.IsRootFolder(null));

        // Root ids must keep the design-doc length (fixed Firefox Places ids).
        foreach (string id in new[]
                 {
                     RootFolders.Toolbar, RootFolders.Menu, RootFolders.Unfiled, RootFolders.Mobile,
                 })
        {
            Assert.Equal(12, id.Length);
        }
    }

    [Fact]
    public void SameContent_IgnoresTimestampsAndTombstoneBits()
    {
        Item a = Fixtures.ValidBookmark(RootFolders.Toolbar);
        Item b = a with
        {
            M = Hlc.Encode(999, 9),
            A = Hlc.Encode(888, 8),
            D = true,
            X = 12345,
        };

        Assert.True(
            Merger.SameContent(a, b),
            "SameContent must not compare M/A/D/X — each side re-saving must not count as a conflict");

        Item c = a with { N = "Another title" };
        Assert.False(Merger.SameContent(a, c));
    }
}
