using System.Text.Json.Serialization;

namespace BookmarkSync.Domain;

/// <summary>Item type.</summary>
public static class ItemTypes
{
    /// <summary>Bookmark: has a URL.</summary>
    public const string Bookmark = "b";

    /// <summary>Folder: no URL.</summary>
    public const string Folder = "f";
}

/// <summary>
/// Smallest unit of sync: one bookmark or one folder.
/// </summary>
/// <remarks>
/// <para>
/// Single-letter field names exist to save bytes: 5000 bookmarks take ~1.1 MB
/// with full names vs ~800 KB with short names, and every sync ships the full state.
/// </para>
/// <para>
/// A <c>readonly record struct</c> (not a class): the merge algorithm relies on
/// <c>s == winner</c> meaning field-by-field equality, matching the Go
/// implementation's value-type semantics. A record struct provides value equality
/// and <c>==</c> for free; with a class every reference comparison would silently
/// report "always equal" and corrupt stats and conflict detection together.
/// </para>
/// </remarks>
public readonly record struct Item
{
    /// <summary>Parent key; top-level items use one of the four root folder ids.</summary>
    [JsonPropertyName("p")]
    [JsonPropertyOrder(0)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string P { get; init; }

    /// <summary>Type, see <see cref="ItemTypes"/>.</summary>
    [JsonPropertyName("t")]
    [JsonPropertyOrder(1)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string T { get; init; }

    /// <summary>Title / folder name.</summary>
    [JsonPropertyName("n")]
    [JsonPropertyOrder(2)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string N { get; init; }

    /// <summary>URL, bookmarks only.</summary>
    [JsonPropertyName("u")]
    [JsonPropertyOrder(3)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string U { get; init; }

    /// <summary>HLC: creation time, excluded from merge comparisons.</summary>
    [JsonPropertyName("a")]
    [JsonPropertyOrder(4)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string A { get; init; }

    /// <summary>HLC: last-modified time, the sole input to LWW comparison.</summary>
    [JsonPropertyName("m")]
    [JsonPropertyOrder(5)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string M { get; init; }

    /// <summary>Deleted flag (tombstone marker).</summary>
    [JsonPropertyName("d")]
    [JsonPropertyOrder(6)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool D { get; init; }

    /// <summary>DeletedAt: wall-clock millis of deletion, used only for GC decisions.</summary>
    [JsonPropertyName("x")]
    [JsonPropertyOrder(7)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long X { get; init; }
}

/// <summary>
/// Fixed ids of the four Firefox system root folders.
/// </summary>
/// <remarks>
/// These ids are stable across Firefox versions and UI languages, so they can be
/// used directly as parent keys of top-level items with no per-device mapping table.
/// See docs/architecture.md §2.
/// </remarks>
public static class RootFolders
{
    /// <summary>Bookmarks toolbar.</summary>
    public const string Toolbar = "toolbar_____";

    /// <summary>Bookmarks menu.</summary>
    public const string Menu = "menu________";

    /// <summary>Other bookmarks.</summary>
    public const string Unfiled = "unfiled_____";

    /// <summary>Mobile bookmarks (excluded from sync by default).</summary>
    public const string Mobile = "mobile______";

    private static readonly HashSet<string> All = new(StringComparer.Ordinal)
    {
        Toolbar, Menu, Unfiled, Mobile,
    };

    public static bool IsRootFolder(string? key) => key is not null && All.Contains(key);
}

/// <summary>Version constants for the persisted format.</summary>
/// <remarks>
/// On a version mismatch the service <b>refuses to start</b> instead of migrating:
/// bookmarks are irreplaceable user data, and silently rewriting them is riskier
/// than asking for one manual upgrade step. See docs/architecture.md §6.
/// <para>
/// Kept in a static class rather than at namespace level: C# does not allow
/// <c>public const</c> members at namespace scope, only on types.
/// </para>
/// </remarks>
public static class Schema
{
    /// <summary>Schema version of state.json / /api/sync bodies.</summary>
    public const int Version = 1;
}

/// <summary>Validation outcome for one state.</summary>
/// <remarks>
/// Mirrors the Go version's <c>([]string, error)</c>. The hard-error vs soft-warning
/// split is explained on <see cref="State.Validate"/>.
/// </remarks>
public readonly record struct ValidationResult(IReadOnlyList<string> Warnings, string? Error)
{
    /// <summary>Whether hard validation passed.</summary>
    public bool IsValid => Error is null;

    public static ValidationResult Ok(IReadOnlyList<string> warnings) => new(warnings, null);

    /// <summary>Hard error: reject the upload outright with no soft warnings.</summary>
    public static ValidationResult Fail(string error) =>
        new(Array.Empty<string>(), error);
}

/// <summary>A full sync state: the in-memory form of state.json.</summary>
public sealed class State
{
    /// <summary>Schema version.</summary>
    [JsonPropertyName("v")]
    [JsonPropertyOrder(0)]
    public int V { get; init; } = Schema.Version;

    /// <summary>Authoritative server HLC; clients use it to calibrate their clocks.</summary>
    /// <remarks>
    /// The C# property is <c>Clock</c> while the wire name is <c>hlc</c>: naming the
    /// property <c>Hlc</c> would collide with the <see cref="Hlc"/> class in
    /// State's scope — member lookup wins over type lookup, so <c>Hlc.Zero</c>
    /// would resolve to "member Zero of this string property", which does not
    /// exist. The wire name stays <c>hlc</c>, so the protocol is unaffected.
    /// </remarks>
    [JsonPropertyName("hlc")]
    [JsonPropertyOrder(1)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Clock { get; init; } = string.Empty;

    /// <summary>Key -&gt; item.</summary>
    [JsonPropertyName("items")]
    [JsonPropertyOrder(2)]
    public Dictionary<string, Item> Items { get; init; } = NewItemsDictionary();

    /// <summary>
    /// Create an empty dictionary with an <see cref="StringComparer.Ordinal"/> comparer.
    /// </summary>
    /// <remarks>
    /// Specified explicitly: Go maps compare string keys byte-wise, and while the
    /// .NET default string comparer is also ordinal, relying on the default leaves
    /// ordering at the mercy of a future CultureInfo change. Merge determinism
    /// depends on key order.
    /// </remarks>
    public static Dictionary<string, Item> NewItemsDictionary() =>
        new(StringComparer.Ordinal);

    public static State New() => new();

    /// <summary>
    /// Return a copy that differs only in <see cref="Clock"/>, <b>sharing</b> the Items dictionary.
    /// </summary>
    /// <remarks>
    /// Not a <c>with</c> expression because <c>State</c> is a plain class, not a record.
    /// A record would auto-generate field-based Equals, but Items is a
    /// <c>Dictionary</c> — compared by <b>reference</b> — so two States with equal
    /// contents but different dictionary objects would compare unequal. That is
    /// exactly what the merge tests assert with <c>sameStates</c>, so the record
    /// form would break "idempotency" assertions in hard-to-diagnose ways.
    /// <para>
    /// Sharing Items is safe here: the caller (store.SyncAsync) never writes into
    /// the dictionary afterwards; it only swaps the Clock before persisting.
    /// </para>
    /// </remarks>
    public State WithClock(string clock) => new() { V = V, Clock = clock, Items = Items };

    /// <summary>Return a deep copy.</summary>
    /// <remarks>
    /// The copy must be deep: the merge places server and incoming items into the
    /// result dictionary. <see cref="Item"/> is a value type so copying it is safe;
    /// the real hazard is <b>sharing the dictionary itself</b> — a later in-place
    /// edit of the result would also corrupt server state. So the dictionary is copied too.
    /// </remarks>
    public State Clone()
    {
        var copy = NewItemsDictionary();
        foreach ((string k, Item v) in Items)
        {
            copy[k] = v;
        }

        return new State { V = V, Clock = Clock, Items = copy };
    }

    /// <summary>Largest HLC in this state (scans every a and m field).</summary>
    /// <remarks>
    /// After each sync the server advances its own HLC past this value, so every
    /// timestamp it emits stays ahead of any client timestamp it has seen.
    /// See docs/architecture.md §5.
    /// </remarks>
    public string MaxHlc()
    {
        string output = Hlc.Zero;
        foreach (Item it in Items.Values)
        {
            if (Hlc.Compare(it.M, output) > 0)
            {
                output = it.M;
            }

            if (Hlc.Compare(it.A, output) > 0)
            {
                output = it.A;
            }
        }

        return output;
    }

    /// <summary>Number of live (non-tombstone) items.</summary>
    public int CountActive()
    {
        int n = 0;
        foreach (Item it in Items.Values)
        {
            if (!it.D)
            {
                n++;
            }
        }

        return n;
    }

    /// <summary>All keys in ascending order.</summary>
    /// <remarks>
    /// Determinism matters: dictionary traversal order depends on insertion order,
    /// while the conflict list and the HLC-collision tiebreak both need a stable
    /// order, or identical inputs could produce different outputs.
    /// </remarks>
    public string[] KeysSorted()
    {
        var keys = new string[Items.Count];
        Items.Keys.CopyTo(keys, 0);
        Array.Sort(keys, StringComparer.Ordinal);
        return keys;
    }

    /// <summary>Depth of the given key (a direct child of a root folder has depth 1).</summary>
    /// <returns>Depth; -1 with a reason in <paramref name="error"/> when the parent chain is broken.</returns>
    public int DepthOf(string key, out string? error)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int depth = 0;
        string cur = key;

        while (true)
        {
            if (!Items.TryGetValue(cur, out Item it))
            {
                error = $"missing parent {cur}";
                return -1;
            }

            if (RootFolders.IsRootFolder(it.P))
            {
                error = null;
                return depth + 1;
            }

            if (!seen.Add(cur))
            {
                error = "parent cycle detected";
                return -1;
            }

            cur = it.P;
            depth++;

            if (depth > Limits.MaxDepth + 1)
            {
                // Bail out early so a crafted long chain cannot make DepthOf run for long.
                error = $"parent chain exceeds {Limits.MaxDepth + 1} levels";
                return depth;
            }
        }
    }

    /// <summary>Check whether a state is safe to merge.</summary>
    /// <remarks>
    /// Strictness is deliberately tiered:
    /// <list type="bullet">
    /// <item>
    /// <b>Hard errors</b> (<see cref="ValidationResult.Error"/> set): inputs that would
    /// break parsing or blow up resources — version mismatch, malformed keys, bad types,
    /// malformed HLCs, over-limit counts/depths/lengths. Such input is corrupt or
    /// hostile and must be rejected.
    /// </item>
    /// <item>
    /// <b>Soft warnings</b> (<see cref="ValidationResult.Warnings"/>, non-blocking): tree
    /// problems — missing parents, non-folder parents, cycles. These should never happen
    /// (collectors always build from a real Firefox tree, so parents must exist), but
    /// rejecting a whole upload over them would be harsh. They are logged as warnings
    /// and the apply phase skips the affected items as a fallback.
    /// </item>
    /// </list>
    /// </remarks>
    public ValidationResult Validate()
    {
        var warnings = new List<string>();

        if (V != Schema.Version)
        {
            return ValidationResult.Fail($"schema version mismatch: expected {Schema.Version}, got {V}");
        }

        if (Items.Count > Limits.MaxItems)
        {
            return ValidationResult.Fail($"item count {Items.Count} exceeds limit {Limits.MaxItems}");
        }

        foreach ((string key, Item it) in Items)
        {
            if (key.Length != Limits.KeyLen || !IsHex(key))
            {
                return ValidationResult.Fail($"invalid item key: \"{key}\" (expected {Limits.KeyLen} hex chars)");
            }

            switch (it.T)
            {
                case ItemTypes.Bookmark:
                    if (string.IsNullOrEmpty(it.U))
                    {
                        return ValidationResult.Fail($"item {key}: bookmark must have url");
                    }

                    if (it.U.Length > Limits.MaxUrlLen)
                    {
                        return ValidationResult.Fail(
                            $"item {key}: url length {it.U.Length} exceeds limit {Limits.MaxUrlLen}");
                    }

                    break;

                case ItemTypes.Folder:
                    if (!string.IsNullOrEmpty(it.U))
                    {
                        return ValidationResult.Fail($"item {key}: folder must not have url");
                    }

                    break;

                default:
                    return ValidationResult.Fail($"item {key}: invalid type \"{it.T}\"");
            }

            if (it.N is not null && it.N.Length > Limits.MaxTitleLen)
            {
                return ValidationResult.Fail(
                    $"item {key}: title length {it.N.Length} exceeds limit {Limits.MaxTitleLen}");
            }

            if (!Hlc.TryDecode(it.M, out _, out _))
            {
                return ValidationResult.Fail($"item {key}: field m is not a valid HLC: \"{it.M}\"");
            }

            if (!string.IsNullOrEmpty(it.A) && !Hlc.TryDecode(it.A, out _, out _))
            {
                return ValidationResult.Fail($"item {key}: field a is not a valid HLC: \"{it.A}\"");
            }

            if (it.D && it.X == 0)
            {
                warnings.Add($"item {key} is a tombstone but missing deletion time x");
            }
        }

        // Tree-structure checks.
        foreach ((string key, Item it) in Items)
        {
            if (RootFolders.IsRootFolder(it.P))
            {
                continue; // Top-level item whose parent is a system root: fine.
            }

            if (!Items.TryGetValue(it.P, out Item parent))
            {
                warnings.Add($"item {key} parent {it.P} does not exist");
                continue;
            }

            if (parent.T != ItemTypes.Folder)
            {
                warnings.Add($"item {key} parent {it.P} is not a folder");
                continue;
            }

            // A live child under a tombstoned parent is always inconsistent: the apply
            // phase cannot attach it anywhere. Record it and move on (the item is
            // safely skipped during apply).
            if (parent.D && !it.D)
            {
                warnings.Add($"item {key} is alive but parent {it.P} is deleted");
            }
        }

        // Depth check (with cycle detection: corrupt data could otherwise loop forever).
        foreach (string key in Items.Keys)
        {
            int d = DepthOf(key, out string? err);
            if (err is not null)
            {
                warnings.Add($"item {key} has broken parent chain: {err}");
                continue;
            }

            if (d > Limits.MaxDepth)
            {
                return ValidationResult.Fail($"item {key} depth {d} exceeds limit {Limits.MaxDepth}");
            }
        }

        return ValidationResult.Ok(warnings);
    }

    private static bool IsHex(string s)
    {
        if (s.Length == 0)
        {
            return false;
        }

        foreach (char ch in s)
        {
            bool digit = ch is >= '0' and <= '9';
            bool lower = ch is >= 'a' and <= 'f';
            if (!digit && !lower)
            {
                return false;
            }
        }

        return true;
    }
}
