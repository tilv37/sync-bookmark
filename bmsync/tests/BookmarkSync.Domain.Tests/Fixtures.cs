using System.Security.Cryptography;
using System.Text;

namespace BookmarkSync.Domain.Tests;

/// <summary>
/// 领域数据的构造器。逐条对应 合并算法的测试用例（服务端从 Go 迁移到 .NET 10 时逐条移植）
/// 里的同名辅助。
/// </summary>
/// <remarks>
/// keyOf 用 <c>"bmsync-test:" + label</c> 做 SHA-256 而不是直接用 label：
/// 测试里的 key 必须是 <see cref="Limits.KeyLen"/> 位十六进制才能通过校验，
/// 而"用标签本身当 key"在造非法 key 的用例时又很方便。派生一层兼顾两者。
/// </remarks>
internal static class Fixtures
{
    public const string DeviceA = "device-a";
    public const string DeviceB = "device-b";

    /// <summary>固定墙钟基准，让涉及 x（删除时间）的断言确定。</summary>
    public const long T0 = 1_700_000_000_000L;

    /// <summary>由标签确定性地生成一个合法 key（32 位十六进制）。</summary>
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

    /// <summary>把一个 item 变成墓碑：d=true、x=删除时间、m=删除时刻。</summary>
    public static Item Tomb(Item it, long at, long ms, int c) => it with { D = true, X = at, M = Hlc.Encode(ms, c) };

    public static Item ValidBookmark(string parent) => Bookmark(parent, "标题", "https://example.com", 100, 0);

    public static Item ValidFolder(string parent) => Folder(parent, "目录", 100, 0);

    public static State StateOf(params (string Label, Item Item)[] items)
    {
        State s = State.New();
        foreach ((string label, Item it) in items)
        {
            s.Items[KeyOf(label)] = it;
        }

        return s;
    }

    /// <summary>直接按 key 放入（用于造非法 key 的用例）。</summary>
    public static State StateOfRaw(params (string Key, Item Item)[] items)
    {
        State s = State.New();
        foreach ((string key, Item it) in items)
        {
            s.Items[key] = it;
        }

        return s;
    }

    /// <summary>深比较两份 state。<see cref="Item"/> 是值类型，== 即全字段相等。</summary>
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

    /// <summary>把 Summary 渲染成可读的短串，便于断言失败时定位。</summary>
    public static string Summarize(Summary s) =>
        $"created={s.Created} updated={s.Updated} deleted={s.Deleted} unchanged={s.Unchanged}";
}
