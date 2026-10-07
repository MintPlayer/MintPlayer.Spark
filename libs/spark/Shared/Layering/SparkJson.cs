using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MintPlayer.Spark.Layering;

// One source, compiled into MintPlayer.Spark.Abstractions (net11.0) and the generators
// (netstandard2.0) — composition D14. No System.Text.Json: the generators cannot load it.

internal enum SparkJsonKind { Object, Array, String, Number, Boolean, Null }

/// <summary>A JSON value. Objects keep their members in order; numbers keep their raw text.</summary>
internal abstract class SparkJsonNode
{
    public abstract SparkJsonKind Kind { get; }

    public abstract SparkJsonNode DeepClone();

    /// <summary>The compact JSON text.</summary>
    public override string ToString() => SparkJson.Write(this);

    /// <summary>Structural equality: member order does not matter, array order does, numbers compare by value.</summary>
    public static bool DeepEquals(SparkJsonNode? a, SparkJsonNode? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null || a.Kind != b.Kind) return false;

        switch (a)
        {
            case SparkJsonString s: return s.Value == ((SparkJsonString)b).Value;
            case SparkJsonBoolean x: return x.Value == ((SparkJsonBoolean)b).Value;
            case SparkJsonNumber n: return SparkJsonNumber.ValueEquals(n.Raw, ((SparkJsonNumber)b).Raw);
            case SparkJsonArray arr:
            {
                var other = (SparkJsonArray)b;
                if (arr.Items.Count != other.Items.Count) return false;
                for (var i = 0; i < arr.Items.Count; i++)
                    if (!DeepEquals(arr.Items[i], other.Items[i])) return false;
                return true;
            }
            case SparkJsonObject obj:
            {
                var other = (SparkJsonObject)b;
                if (obj.Count != other.Count) return false;
                foreach (var member in obj.Members)
                    if (!other.TryGetValue(member.Key, out var value) || !DeepEquals(member.Value, value)) return false;
                return true;
            }
            default: return true; // null
        }
    }
}

/// <summary>An object: members in the order they were added, looked up through <see cref="Comparer"/>.</summary>
internal sealed class SparkJsonObject : SparkJsonNode
{
    private readonly List<string> order = new();
    private readonly Dictionary<string, KeyValuePair<string, SparkJsonNode>> members;

    public SparkJsonObject() : this(StringComparer.Ordinal) { }

    public SparkJsonObject(StringComparer comparer)
    {
        Comparer = comparer;
        members = new Dictionary<string, KeyValuePair<string, SparkJsonNode>>(comparer);
    }

    public StringComparer Comparer { get; }

    public override SparkJsonKind Kind => SparkJsonKind.Object;

    public int Count => order.Count;

    /// <summary>The members in order, each under the spelling it was first added with.</summary>
    public IEnumerable<KeyValuePair<string, SparkJsonNode>> Members
    {
        get
        {
            foreach (var key in order)
                yield return members[key];
        }
    }

    public SparkJsonNode? this[string key] => members.TryGetValue(key, out var member) ? member.Value : null;

    /// <summary>
    /// The member <paramref name="key"/> names under <paramref name="comparer"/>, the kind's own rules.
    /// A parsed layer's objects are ordinal, so the indexer alone would miss <c>"Key"</c> in a kind
    /// whose names ignore case.
    /// </summary>
    public SparkJsonNode? Get(string key, StringComparer comparer)
    {
        if (members.TryGetValue(key, out var member)) return member.Value;
        if (ReferenceEquals(comparer, Comparer)) return null;
        foreach (var candidate in Members)
            if (comparer.Equals(candidate.Key, key)) return candidate.Value;
        return null;
    }

    public bool TryGetValue(string key, out SparkJsonNode value)
    {
        var found = members.TryGetValue(key, out var member);
        value = member.Value;
        return found;
    }

    /// <summary>The member <paramref name="key"/> matches, with the spelling it is stored under.</summary>
    public bool TryGetMember(string key, out string spelling, out SparkJsonNode value)
    {
        var found = members.TryGetValue(key, out var member);
        spelling = member.Key;
        value = member.Value;
        return found;
    }

    /// <summary>Adds a member, or replaces an existing one in its place and under its first spelling.</summary>
    public void Set(string key, SparkJsonNode value)
    {
        if (members.TryGetValue(key, out var existing))
        {
            members[key] = new KeyValuePair<string, SparkJsonNode>(existing.Key, value);
            return;
        }

        members[key] = new KeyValuePair<string, SparkJsonNode>(key, value);
        order.Add(key);
    }

    /// <summary>Adds a member; false when the key is already present.</summary>
    public bool TryAdd(string key, SparkJsonNode value)
    {
        if (members.ContainsKey(key)) return false;
        Set(key, value);
        return true;
    }

    public bool Remove(string key)
    {
        if (!members.TryGetValue(key, out var existing)) return false;
        members.Remove(key);
        order.Remove(existing.Key);
        return true;
    }

    public override SparkJsonNode DeepClone()
    {
        var clone = new SparkJsonObject(Comparer);
        foreach (var member in Members)
            clone.Set(member.Key, member.Value.DeepClone());
        return clone;
    }
}

internal sealed class SparkJsonArray : SparkJsonNode
{
    public List<SparkJsonNode> Items { get; } = new();

    public override SparkJsonKind Kind => SparkJsonKind.Array;

    public override SparkJsonNode DeepClone()
    {
        var clone = new SparkJsonArray();
        foreach (var item in Items)
            clone.Items.Add(item.DeepClone());
        return clone;
    }
}

internal sealed class SparkJsonString(string value) : SparkJsonNode
{
    public string Value { get; } = value;

    public override SparkJsonKind Kind => SparkJsonKind.String;

    public override SparkJsonNode DeepClone() => new SparkJsonString(Value);
}

/// <summary>A number as written, so <c>1.50</c> round-trips as <c>1.50</c>.</summary>
internal sealed class SparkJsonNumber(string raw) : SparkJsonNode
{
    public string Raw { get; } = raw;

    public override SparkJsonKind Kind => SparkJsonKind.Number;

    public override SparkJsonNode DeepClone() => new SparkJsonNumber(Raw);

    internal static bool ValueEquals(string a, string b)
    {
        if (a == b) return true;
        if (decimal.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            && decimal.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            return x == y;
        return double.Parse(a, NumberStyles.Float, CultureInfo.InvariantCulture)
            .Equals(double.Parse(b, NumberStyles.Float, CultureInfo.InvariantCulture));
    }
}

internal sealed class SparkJsonBoolean : SparkJsonNode
{
    public static readonly SparkJsonBoolean True = new(true);
    public static readonly SparkJsonBoolean False = new(false);

    private SparkJsonBoolean(bool value) => Value = value;

    public bool Value { get; }

    public override SparkJsonKind Kind => SparkJsonKind.Boolean;

    public override SparkJsonNode DeepClone() => new SparkJsonBoolean(Value);
}

internal sealed class SparkJsonNull : SparkJsonNode
{
    public static readonly SparkJsonNull Instance = new();

    private SparkJsonNull() { }

    public override SparkJsonKind Kind => SparkJsonKind.Null;

    public override SparkJsonNode DeepClone() => new SparkJsonNull();
}

/// <summary>The text is not JSON, or states a key twice. The message says where.</summary>
internal sealed class SparkJsonException(string message) : FormatException(message);

/// <summary>
/// Strict JSON (RFC 8259): no comments, no trailing commas, no duplicate keys. The writer is
/// deterministic: members in order, numbers as written, only the characters JSON requires escaped.
/// </summary>
internal static class SparkJson
{
    private const int MaxDepth = 128;

    /// <exception cref="SparkJsonException">The text is not one JSON value, or an object states a key twice.</exception>
    public static SparkJsonNode Parse(string text)
    {
        var reader = new Reader(text);
        reader.SkipWhitespace();
        var value = reader.ReadValue("", 0);
        reader.SkipWhitespace();
        if (!reader.AtEnd)
            throw reader.Error("unexpected content after the JSON value");
        return value;
    }

    public static string Write(SparkJsonNode node, bool indented = false)
    {
        var builder = new StringBuilder();
        Write(builder, node, indented, 0);
        return builder.ToString();
    }

    private static void Write(StringBuilder builder, SparkJsonNode node, bool indented, int depth)
    {
        switch (node)
        {
            case SparkJsonString s:
                WriteString(builder, s.Value);
                break;
            case SparkJsonNumber n:
                builder.Append(n.Raw);
                break;
            case SparkJsonBoolean b:
                builder.Append(b.Value ? "true" : "false");
                break;
            case SparkJsonArray array:
            {
                if (array.Items.Count == 0) { builder.Append("[]"); break; }
                builder.Append('[');
                for (var i = 0; i < array.Items.Count; i++)
                {
                    if (i > 0) builder.Append(',');
                    NewLine(builder, indented, depth + 1);
                    Write(builder, array.Items[i], indented, depth + 1);
                }
                NewLine(builder, indented, depth);
                builder.Append(']');
                break;
            }
            case SparkJsonObject obj:
            {
                if (obj.Count == 0) { builder.Append("{}"); break; }
                builder.Append('{');
                var first = true;
                foreach (var member in obj.Members)
                {
                    if (!first) builder.Append(',');
                    first = false;
                    NewLine(builder, indented, depth + 1);
                    WriteString(builder, member.Key);
                    builder.Append(indented ? ": " : ":");
                    Write(builder, member.Value, indented, depth + 1);
                }
                NewLine(builder, indented, depth);
                builder.Append('}');
                break;
            }
            default:
                builder.Append("null");
                break;
        }
    }

    private static void NewLine(StringBuilder builder, bool indented, int depth)
    {
        if (!indented) return;
        builder.Append('\n').Append(' ', depth * 2);
    }

    public static void WriteString(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                default:
                    if (c < ' ') builder.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    else builder.Append(c);
                    break;
            }
        }
        builder.Append('"');
    }

    private sealed class Reader(string text)
    {
        private int pos;

        public bool AtEnd => pos >= text.Length;

        public void SkipWhitespace()
        {
            while (pos < text.Length && text[pos] is ' ' or '\t' or '\r' or '\n')
                pos++;
        }

        public SparkJsonException Error(string problem)
        {
            int line = 1, column = 1;
            for (var i = 0; i < pos && i < text.Length; i++)
            {
                if (text[i] == '\n') { line++; column = 1; }
                else column++;
            }
            return new SparkJsonException($"{problem} (line {line}, column {column})");
        }

        public SparkJsonNode ReadValue(string path, int depth)
        {
            if (depth > MaxDepth) throw Error($"nested deeper than {MaxDepth} levels");
            if (AtEnd) throw Error("unexpected end of input");

            switch (text[pos])
            {
                case '{': return ReadObject(path, depth);
                case '[': return ReadArray(path, depth);
                case '"': return new SparkJsonString(ReadString());
                case 't': Literal("true"); return SparkJsonBoolean.True;
                case 'f': Literal("false"); return SparkJsonBoolean.False;
                case 'n': Literal("null"); return SparkJsonNull.Instance;
                default:
                    if (text[pos] == '-' || IsDigit(text[pos])) return ReadNumber();
                    throw Error($"unexpected character '{text[pos]}'");
            }
        }

        private void Literal(string literal)
        {
            if (string.CompareOrdinal(text, pos, literal, 0, literal.Length) != 0)
                throw Error("unexpected token");
            pos += literal.Length;
        }

        private SparkJsonObject ReadObject(string path, int depth)
        {
            pos++; // {
            var obj = new SparkJsonObject();
            SkipWhitespace();
            if (!AtEnd && text[pos] == '}') { pos++; return obj; }

            while (true)
            {
                SkipWhitespace();
                if (AtEnd || text[pos] != '"') throw Error("expected a property name");
                var keyStart = pos;
                var key = ReadString();
                var childPath = path.Length == 0 ? key : path + "." + key;
                SkipWhitespace();
                if (AtEnd || text[pos] != ':') throw Error("expected ':' after a property name");
                pos++;
                SkipWhitespace();
                var value = ReadValue(childPath, depth + 1);
                if (!obj.TryAdd(key, value))
                {
                    pos = keyStart;
                    throw Error($"'{childPath}' is stated twice");
                }
                SkipWhitespace();
                if (AtEnd) throw Error("unexpected end of input in an object");
                if (text[pos] == ',') { pos++; continue; }
                if (text[pos] == '}') { pos++; return obj; }
                throw Error("expected ',' or '}'");
            }
        }

        private SparkJsonArray ReadArray(string path, int depth)
        {
            pos++; // [
            var array = new SparkJsonArray();
            SkipWhitespace();
            if (!AtEnd && text[pos] == ']') { pos++; return array; }

            while (true)
            {
                SkipWhitespace();
                array.Items.Add(ReadValue(path + "[" + array.Items.Count.ToString(CultureInfo.InvariantCulture) + "]", depth + 1));
                SkipWhitespace();
                if (AtEnd) throw Error("unexpected end of input in an array");
                if (text[pos] == ',') { pos++; continue; }
                if (text[pos] == ']') { pos++; return array; }
                throw Error("expected ',' or ']'");
            }
        }

        private string ReadString()
        {
            pos++; // opening quote
            var builder = new StringBuilder();
            while (true)
            {
                if (AtEnd) throw Error("unterminated string");
                var c = text[pos];
                if (c == '"') { pos++; return builder.ToString(); }
                if (c < ' ') throw Error("unescaped control character in a string");
                if (c != '\\') { builder.Append(c); pos++; continue; }

                pos++;
                if (AtEnd) throw Error("unterminated string");
                switch (text[pos])
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        if (pos + 4 >= text.Length
                            || !int.TryParse(text.Substring(pos + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
                            throw Error("invalid \\u escape");
                        builder.Append((char)code);
                        pos += 4;
                        break;
                    default:
                        throw Error($"invalid escape '\\{text[pos]}'");
                }
                pos++;
            }
        }

        private SparkJsonNumber ReadNumber()
        {
            var start = pos;
            if (text[pos] == '-') pos++;
            if (AtEnd || !IsDigit(text[pos])) throw Error("invalid number");
            if (text[pos] == '0')
            {
                pos++;
                if (!AtEnd && IsDigit(text[pos])) throw Error("a number cannot start with 0");
            }
            else
            {
                while (!AtEnd && IsDigit(text[pos])) pos++;
            }

            if (!AtEnd && text[pos] == '.')
            {
                pos++;
                if (AtEnd || !IsDigit(text[pos])) throw Error("invalid number");
                while (!AtEnd && IsDigit(text[pos])) pos++;
            }

            if (!AtEnd && text[pos] is 'e' or 'E')
            {
                pos++;
                if (!AtEnd && text[pos] is '+' or '-') pos++;
                if (AtEnd || !IsDigit(text[pos])) throw Error("invalid number");
                while (!AtEnd && IsDigit(text[pos])) pos++;
            }

            return new SparkJsonNumber(text.Substring(start, pos - start));
        }

        private static bool IsDigit(char c) => c >= '0' && c <= '9';
    }
}
