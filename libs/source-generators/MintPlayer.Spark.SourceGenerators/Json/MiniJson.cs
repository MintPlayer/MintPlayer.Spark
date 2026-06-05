using System;
using System.Collections.Generic;
using System.Text;

namespace MintPlayer.Spark.SourceGenerators.Json;

/// <summary>
/// Minimal JSON parser/serializer. Only handles objects, strings, and null — the subset
/// used by translations.json. Arrays/numbers/booleans are rejected at parse time so the
/// generator can emit a precise diagnostic rather than silently accepting them.
/// </summary>
internal abstract class JsonNode
{
    public string Path { get; set; } = "";
}

internal sealed class JsonObject : JsonNode
{
    public List<KeyValuePair<string, JsonNode>> Members { get; } = new();
}

internal sealed class JsonString : JsonNode
{
    public string Value { get; set; } = "";
}

internal sealed class JsonNull : JsonNode { }

internal sealed class JsonParseException : Exception
{
    public JsonParseException(string message) : base(message) { }
}

internal static class MiniJson
{
    public static JsonNode Parse(string text)
    {
        var pos = 0;
        SkipWhitespace(text, ref pos);
        var node = ParseValue(text, ref pos, "");
        SkipWhitespace(text, ref pos);
        if (pos < text.Length)
            throw new JsonParseException($"Unexpected trailing content at offset {pos}.");
        return node;
    }

    private static JsonNode ParseValue(string s, ref int pos, string path)
    {
        SkipWhitespace(s, ref pos);
        if (pos >= s.Length) throw new JsonParseException("Unexpected end of input.");
        var c = s[pos];
        switch (c)
        {
            case '{': return ParseObject(s, ref pos, path);
            case '"': return new JsonString { Value = ParseString(s, ref pos), Path = path };
            case 'n':
                if (pos + 4 <= s.Length && s.Substring(pos, 4) == "null")
                {
                    pos += 4;
                    return new JsonNull { Path = path };
                }
                throw new JsonParseException($"Unexpected token at offset {pos}.");
            case '[':
                throw new JsonParseException($"Arrays are not allowed in translations.json (at '{path}').");
            case 't': case 'f':
                throw new JsonParseException($"Booleans are not allowed in translations.json (at '{path}').");
            default:
                if (c == '-' || (c >= '0' && c <= '9'))
                    throw new JsonParseException($"Numbers are not allowed in translations.json (at '{path}').");
                throw new JsonParseException($"Unexpected character '{c}' at offset {pos}.");
        }
    }

    private static JsonObject ParseObject(string s, ref int pos, string path)
    {
        if (s[pos] != '{') throw new JsonParseException("Expected '{'.");
        pos++;
        var obj = new JsonObject { Path = path };
        SkipWhitespace(s, ref pos);
        if (pos < s.Length && s[pos] == '}') { pos++; return obj; }

        while (true)
        {
            SkipWhitespace(s, ref pos);
            if (pos >= s.Length || s[pos] != '"')
                throw new JsonParseException($"Expected property name at offset {pos}.");
            var key = ParseString(s, ref pos);
            SkipWhitespace(s, ref pos);
            if (pos >= s.Length || s[pos] != ':')
                throw new JsonParseException($"Expected ':' after property name at offset {pos}.");
            pos++;
            var childPath = string.IsNullOrEmpty(path) ? key : path + "." + key;
            var value = ParseValue(s, ref pos, childPath);
            obj.Members.Add(new KeyValuePair<string, JsonNode>(key, value));
            SkipWhitespace(s, ref pos);
            if (pos >= s.Length) throw new JsonParseException("Unexpected end of object.");
            if (s[pos] == ',') { pos++; continue; }
            if (s[pos] == '}') { pos++; return obj; }
            throw new JsonParseException($"Expected ',' or '}}' at offset {pos}.");
        }
    }

    private static string ParseString(string s, ref int pos)
    {
        if (s[pos] != '"') throw new JsonParseException($"Expected '\"' at offset {pos}.");
        pos++;
        var sb = new StringBuilder();
        while (pos < s.Length)
        {
            var c = s[pos++];
            if (c == '"') return sb.ToString();
            if (c == '\\')
            {
                if (pos >= s.Length) throw new JsonParseException("Unterminated escape sequence.");
                var esc = s[pos++];
                switch (esc)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (pos + 4 > s.Length) throw new JsonParseException("Truncated unicode escape.");
                        var hex = s.Substring(pos, 4);
                        pos += 4;
                        if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out var code))
                            throw new JsonParseException($"Invalid unicode escape '\\u{hex}'.");
                        sb.Append((char)code);
                        break;
                    default:
                        throw new JsonParseException($"Invalid escape sequence '\\{esc}'.");
                }
            }
            else
            {
                sb.Append(c);
            }
        }
        throw new JsonParseException("Unterminated string.");
    }

    private static void SkipWhitespace(string s, ref int pos)
    {
        while (pos < s.Length)
        {
            var c = s[pos];
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { pos++; continue; }
            break;
        }
    }

    /// <summary>
    /// Serializes a dictionary of (dottedKey → (language → template)) as a compact JSON object.
    /// </summary>
    public static string Serialize(IReadOnlyList<KeyValuePair<string, IReadOnlyList<KeyValuePair<string, string>>>> entries)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        for (var i = 0; i < entries.Count; i++)
        {
            if (i > 0) sb.Append(',');
            AppendString(sb, entries[i].Key);
            sb.Append(':');
            sb.Append('{');
            var langs = entries[i].Value;
            for (var j = 0; j < langs.Count; j++)
            {
                if (j > 0) sb.Append(',');
                AppendString(sb, langs[j].Key);
                sb.Append(':');
                AppendString(sb, langs[j].Value);
            }
            sb.Append('}');
        }
        sb.Append('}');
        return sb.ToString();
    }

    public static void AppendString(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                        sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "\\u{0:x4}", (int)c);
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
