using System.Text.Json;
using System.Text.Json.Serialization;

namespace BookmarkSync.Domain;

/// <summary>
/// <see cref="Item"/> 的 JSON 转换器，逐字节对齐 Go 的 <c>encoding/json</c> 行为。
/// </summary>
/// <remarks>
/// <para>
/// 手写而不是靠 <c>[JsonIgnore(Condition = WhenWritingDefault)]</c>，原因是一个
/// 已实测确认的坑：
/// </para>
/// <list type="bullet">
/// <item>
/// <b>空串不会被省略。</b>C# 的 <c>WhenWritingDefault</c> 对 <c>string</c> 判断的是
/// <c>null</c>，而 <c>""</c> 不是 null，所以它照样会写出 <c>"u":""</c>；
/// Go 的 <c>omitempty</c> 对字符串判断 <c>== ""</c>，会省略。
/// 已用最小复现程序确认，不靠记忆。
/// </item>
/// <item>
/// <b>非 ASCII 转义。</b>.NET 默认把中文字符写成 <c>中</c>（6 倍体积），
/// 而 Go 原样输出 UTF-8。书签标题大量含中文，5000 条书签的
/// 请求体会从约 800KB 涨到 4MB 以上，直接撞上 nginx 的
/// <c>client_max_body_size</c>。解决办法是全局
/// <see cref="JsonSerializerOptions.Encoder"/> 用
/// <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>（见
/// <see cref="BmsyncJson"/>）。
/// </item>
/// </list>
/// <para>
/// 字段顺序按 Go 结构体声明顺序写出：p t n u a m d x。不是为了兼容
/// （JSON 解析器不在乎顺序），而是为了让 state.json 的人工 diff 可读、
/// 且能与 Go 版逐字节对照。
/// </para>
/// </remarks>
public sealed class ItemJsonConverter : JsonConverter<Item>
{
    public override Item Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"item 应为 JSON 对象，实际是 {reader.TokenType}");
        }

        string p = string.Empty, t = string.Empty, n = string.Empty, u = string.Empty;
        string a = string.Empty, m = string.Empty;
        bool d = false;
        long x = 0;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return new Item { P = p, T = t, N = n, U = u, A = a, M = m, D = d, X = x };
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException($"item 内出现意外的 {reader.TokenType}");
            }

            string prop = reader.GetString()!;
            if (!reader.Read())
            {
                throw new JsonException($"item 字段 {prop} 后缺少值");
            }

            switch (prop)
            {
                // 缺失字段与 null 都当空串，与 Go 把缺字段留成零值 "" 的行为一致。
                // 之所以容忍 null：客户端漏发字段不该让整份上传被拒 ——
                // 硬错误应该留给"格式非法"（见 State.Validate）而不是"字段没来"。
                case "p": p = ReadStringOrEmpty(ref reader); break;
                case "t": t = ReadStringOrEmpty(ref reader); break;
                case "n": n = ReadStringOrEmpty(ref reader); break;
                case "u": u = ReadStringOrEmpty(ref reader); break;
                case "a": a = ReadStringOrEmpty(ref reader); break;
                case "m": m = ReadStringOrEmpty(ref reader); break;
                case "d": d = reader.TokenType == JsonTokenType.True; break;
                case "x":
                    x = reader.TokenType == JsonTokenType.Number ? reader.GetInt64() : 0;
                    break;
                default:
                    // 未知字段的处理取决于上下文：解析客户端请求要报错
                    // （字段名拼错必须立刻暴露），读自己写出的 state.json 要放过
                    // （它是持久化格式，加字段后旧版本读到新文件不该崩）。
                    //
                    // UnmappedMemberHandling 这个选项**只对反射式反序列化生效**，
                    // 自定义转换器必须自己读它。漏读的症状是"严格模式形同虚设"，
                    // 而且测不出来 —— 请求照样成功，只是拼错的字段被静默忽略。
                    if (options.UnmappedMemberHandling == JsonUnmappedMemberHandling.Disallow)
                    {
                        throw new JsonException($"item 含未知字段 \"{prop}\"");
                    }

                    reader.Skip();
                    break;
            }
        }

        throw new JsonException("item 对象没有正常结束");
    }

    public override void Write(Utf8JsonWriter writer, Item value, JsonSerializerOptions options) =>
        WriteItem(writer, value, options);

    /// <summary>
    /// 写一个 item 的实际实现。internal 供 <see cref="StateJsonConverter"/> 直接调用，
    /// 以避开 <c>JsonSerializer.Serialize</c> 泛型重载的 AOT 警告（IL2026/IL3050）。
    /// </summary>
    internal static void WriteItem(Utf8JsonWriter writer, Item value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        WriteString(writer, "p", value.P);
        WriteString(writer, "t", value.T);
        WriteString(writer, "n", value.N);
        WriteString(writer, "u", value.U);
        WriteString(writer, "a", value.A);
        WriteString(writer, "m", value.M);
        if (value.D)
        {
            writer.WriteBoolean("d", true);
        }

        if (value.X != 0)
        {
            writer.WriteNumber("x", value.X);
        }

        writer.WriteEndObject();
    }

    private static string ReadStringOrEmpty(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return string.Empty;
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"期望字符串，实际是 {reader.TokenType}");
        }

        return reader.GetString() ?? string.Empty;
    }

    private static void WriteString(Utf8JsonWriter writer, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            writer.WriteString(name, value);
        }
    }
}

/// <summary>
/// <see cref="State"/> 的 JSON 转换器：把 items 的 key <b>排序后</b>写出。
/// </summary>
/// <remarks>
/// Go 的 <c>encoding/json</c> 序列化 map 时会把 key 排序输出；.NET 的
/// <c>Dictionary</c> 按插入顺序遍历。不对齐的后果不是"解析出错"——那不会发生，
/// 而是 state.json 里 key 顺序随机：每次同步重写文件后 <c>git diff</c> 满屏
/// 噪声，而且两套实现（曾经有 Go 与 C# 两份）无法逐字节对照。
/// </remarks>
public sealed class StateJsonConverter : JsonConverter<State>
{
    public override State Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"state 应为 JSON 对象，实际是 {reader.TokenType}");
        }

        int v = 0;
        string hlc = string.Empty;
        Dictionary<string, Item>? items = null;

        var itemConverter = new ItemJsonConverter();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return new State
                {
                    V = v,
                    Clock = hlc,
                    Items = items ?? State.NewItemsDictionary(),
                };
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException($"state 内出现意外的 {reader.TokenType}");
            }

            string prop = reader.GetString()!;
            if (!reader.Read())
            {
                throw new JsonException($"state 字段 {prop} 后缺少值");
            }

            switch (prop)
            {
                case "v":
                    v = reader.TokenType == JsonTokenType.Number ? reader.GetInt32() : 0;
                    break;

                case "hlc":
                    hlc = reader.TokenType == JsonTokenType.String ? reader.GetString() ?? string.Empty : string.Empty;
                    break;

                case "items":
                    if (reader.TokenType == JsonTokenType.Null)
                    {
                        items = State.NewItemsDictionary();
                        break;
                    }

                    items = ReadItems(ref reader, itemConverter, options);
                    break;

                default:
                    if (options.UnmappedMemberHandling == JsonUnmappedMemberHandling.Disallow)
                    {
                        throw new JsonException($"state 含未知字段 \"{prop}\"");
                    }

                    reader.Skip();
                    break;
            }
        }

        throw new JsonException("state 对象没有正常结束");
    }

    public override void Write(Utf8JsonWriter writer, State value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("v", value.V);
        if (!string.IsNullOrEmpty(value.Clock))
        {
            writer.WriteString("hlc", value.Clock);
        }

        writer.WritePropertyName("items");
        writer.WriteStartObject();

        foreach (string k in value.KeysSorted())
        {
            writer.WritePropertyName(k);
            WriteItem(writer, value.Items[k], options);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    /// <summary>
    /// 写一个 item —— 直接调 <see cref="ItemJsonConverter"/> 而不是
    /// <c>JsonSerializer.Serialize(writer, item, options)</c>。
    /// </summary>
    /// <remarks>
    /// 后者带 <c>[RequiresUnreferencedCode]</c> 与
    /// <c>[RequiresDynamicCode]</c>，在 PublishAot 下会报 IL2026 / IL3050
    /// 两个错误。它们说的是"JSON 序列化需要运行时生成代码" ——
    /// 而这里根本不需要运行时生成任何代码：转换器是我手写的，
    /// 字段名是写死的。
    /// <para>
    /// 也就是说这个错误是<b>误报</b>：泛型重载为了通用性做了保守假设，
    /// 而我们已经精确知道要用哪个转换器。绕过它的正确做法是显式调用，
    /// 不是加 <c>NoWarn</c> —— 后者会让真正的 AOT 不兼容问题也一起被吞掉。
    /// </para>
    /// </remarks>
    private static void WriteItem(Utf8JsonWriter writer, Item item, JsonSerializerOptions options) =>
        ItemJsonConverter.WriteItem(writer, item, options);

    private static Dictionary<string, Item> ReadItems(
        ref Utf8JsonReader reader,
        ItemJsonConverter itemConverter,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"items 应为 JSON 对象，实际是 {reader.TokenType}");
        }

        var items = State.NewItemsDictionary();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return items;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException($"items 内出现意外的 {reader.TokenType}");
            }

            string key = reader.GetString()!;
            if (!reader.Read())
            {
                throw new JsonException($"item {key} 后缺少值");
            }

            items[key] = itemConverter.Read(ref reader, typeof(Item), options);
        }

        throw new JsonException("items 对象没有正常结束");
    }
}
