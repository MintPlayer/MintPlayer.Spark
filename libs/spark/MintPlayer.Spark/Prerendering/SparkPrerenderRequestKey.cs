using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MintPlayer.Spark.Prerendering;

/// <summary>
/// The key a supplied response is filed under: <c>METHOD pathAndQuery canonicalBody</c>. It must equal
/// what <c>sparkServerRequestKey</c> (<c>@mintplayer/ng-spark/ssr</c>, spark-server-request-key.ts)
/// computes in the render byte for byte, or the page misses the response (PRD I1).
/// <para>
/// The canonical body is <c>JSON.stringify</c> of the body with object keys sorted ordinally (UTF-16
/// code units, as <c>Array.prototype.sort</c> compares) at every depth. A null member is dropped: on the
/// C# side it stands for a member the page leaves <c>undefined</c>. Strings are escaped as
/// <c>JSON.stringify</c> escapes them, which System.Text.Json's encoders do not.
/// </para>
/// </summary>
internal static class SparkPrerenderRequestKey
{
    private static readonly JsonSerializerOptions BodyOptions = new(JsonSerializerDefaults.Web);

    public static string For(string method, string pathAndQuery, string canonicalBody)
        => $"{method.ToUpperInvariant()} {pathAndQuery} {canonicalBody}";

    /// <summary>The canonical JSON of <paramref name="body"/>, or the empty string for no body.</summary>
    public static string CanonicalBody(object? body)
    {
        if (body is null)
            return "";
        // Parsed rather than SerializeToNode: the Web defaults make that JsonObject case-insensitive,
        // so it rejects keys differing only in case ("b" and "B"), which JavaScript keeps apart.
        var node = body as JsonNode ?? JsonNode.Parse(JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), BodyOptions));
        if (node is null)
            return "";
        var builder = new StringBuilder();
        Write(builder, node);
        return builder.ToString();
    }

    private static void Write(StringBuilder builder, JsonNode? node)
    {
        switch (node)
        {
            case null:
                builder.Append("null");
                break;
            case JsonObject obj:
                builder.Append('{');
                var first = true;
                foreach (var (key, value) in obj.Where(p => p.Value is not null).OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (!first) builder.Append(',');
                    first = false;
                    WriteString(builder, key);
                    builder.Append(':');
                    Write(builder, value);
                }
                builder.Append('}');
                break;
            case JsonArray array:
                builder.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0) builder.Append(',');
                    Write(builder, array[i]);
                }
                builder.Append(']');
                break;
            default:
                switch (node.GetValueKind())
                {
                    case JsonValueKind.String: WriteString(builder, node.GetValue<string>()); break;
                    case JsonValueKind.True: builder.Append("true"); break;
                    case JsonValueKind.False: builder.Append("false"); break;
                    case JsonValueKind.Null: builder.Append("null"); break;
                    case JsonValueKind.Number: WriteNumber(builder, JsonDocument.Parse(node.ToJsonString()).RootElement); break;
                    default: throw new InvalidOperationException($"Unexpected JSON value kind {node.GetValueKind()}.");
                }
                break;
        }
    }

    /// <summary>As JavaScript prints a number: integers plainly, others in the shortest round-trip form.</summary>
    private static void WriteNumber(StringBuilder builder, JsonElement element)
    {
        if (element.TryGetInt64(out var integer))
        {
            builder.Append(integer.ToString(CultureInfo.InvariantCulture));
            return;
        }
        var number = element.GetDouble();
        if (number == Math.Floor(number) && Math.Abs(number) < 1e21)
            builder.Append(number.ToString("0", CultureInfo.InvariantCulture));
        else
            builder.Append(number.ToString("R", CultureInfo.InvariantCulture));
    }

    /// <summary><c>JSON.stringify</c>'s string escaping (ECMA-262 QuoteJSONString).</summary>
    private static void WriteString(StringBuilder builder, string value)
    {
        builder.Append('"');
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (c < 0x20)
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                        builder.Append(c).Append(value[++i]);
                    else if (char.IsSurrogate(c))
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        builder.Append(c);
                    break;
            }
        }
        builder.Append('"');
    }
}
