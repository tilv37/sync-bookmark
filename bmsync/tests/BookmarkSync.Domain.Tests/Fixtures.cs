using System.Security.Cryptography;
using System.Text;

namespace BookmarkSync.Domain.Tests;

/// <summary>
/// Builders for domain test data. Each mirrors the same-named helper in the merge-algorithm
/// test cases (ported one by one when the server moved from Go to .NET 10).
/// </summary>
/// <remarks>
/// KeyOf hashes <c>"bmsync-test:" + label</c> with SHA-256 instead of using the label directly:
/// keys in tests must be <see cref="Limits.KeyLen"/> hex chars to pass validation,
/// while "the label itself as key" is handy for malformed-key cases. Deriving serves both.
/// </remarks>
internal static class Fixtures
{
    public const string DeviceA = "device-a";
    public const string DeviceB = "device-b";

    /// <summary>Fixed wall-clock baseline so assertions involving x (deletion time) are deterministic.</summary>
    public const long T0 = 1_700_000_000_000L;

    /// <summary>Deterministically derive a valid key (32 hex chars) from a label.</summary>
    public static string KeyOf(string label)
    {
        byte[] sum = SHA256.HashData(Encoding.UTF8.GetBytes("bmsync-test:" + label));
        return Convert.ToHexStringLower(sum.AsSpan(0, 16));
    }

    public static Item Bookmark(string parent, string title, string url, long ms = 100, int c = 0) => new()
    {
        P = parent,
        T = ItemTypes.Bookmark,
        N = title,
        U = url,
        M = Hlc.Encode(ms, c),
        A = Hlc.Encode(ms, 0),
    };

    public static Item Folder(string parent, string title, long ms = 100, int c = 0) => new()
    {
        P = parent,
        T = ItemTypes.Folder,
        N = title,
        M = Hlc.Encode(ms, c),
        A = Hlc.Encode(ms, 0),
    };

    /// <summary>Turn an item into a tombstone: d=true, x=deletion time, m=deletion instant.</summary>
    public static Item Tomb(Item it, long at, long ms, int c) => it with { D = true, X = at, M = Hlc.Encode(ms, c) };

    public static Item ValidBookmark(string parent) => Bookmark(parent, "Title", "https://example.com", 100, 0);

    public static Item ValidFolder(string parent) => Folder(parent, "Folder", 100, 0);

    public static State StateOf(params (string Label, Item Item)[] items)
    {
        State s = State.New();
        foreach ((string label, Item it) in items)
        {
            s.Items[KeyOf(label)] = it;
        }

        return s;
    }

    /// <summary>Insert by key directly (for malformed-key cases).</summary>
    public static State StateOfRaw(params (string Key, Item Item)[] items)
    {
        State s = State.New();
        foreach ((string key, Item it) in items)
        {
            s.Items[key] = it;
        }

        return s;
    }

    /// <summary>Deep-compare two states. <see cref="Item"/> is a value type, so == is field-by-field equality.</summary>
    public static bool SameStates(State a, State b)
    {
        if (a.Items.Count != b.Items.Count)
        {
            return false;
        }

        foreach ((string k, Item va) in a.Items)
        {
            if (!b.Items.TryGetValue(k, out Item vb) || va != vb)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Render a Summary as a short readable string for failure triage.</summary>
    public static string Summarize(Summary s) =>
        $"created={s.Created} updated={s.Updated} deleted={s.Deleted} unchanged={s.Unchanged}";
}
