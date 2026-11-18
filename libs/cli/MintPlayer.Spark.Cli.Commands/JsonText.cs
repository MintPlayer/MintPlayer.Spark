using System.Text.Json;

namespace MintPlayer.Spark.Cli;

/// <summary>
/// Edits an App_Data JSON file as text: it inserts one member or one array item and leaves every other byte alone, so
/// comments, key order and the author's layout survive. The template engine's JSON post-action rewrites the whole file
/// through <c>JsonNode</c>, which drops comments, and it cannot append to an array (plan M3 spike). Comments
/// (<c>//</c>, <c>/* */</c>) and trailing commas are accepted, as Spark's own loaders accept them.
/// </summary>
internal static class JsonText
{
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Whether the root object has a member <paramref name="name"/>.</summary>
    public static bool HasProperty(string json, string name)
    {
        using var document = JsonDocument.Parse(json, ReadOptions);
        return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(name, out _);
    }

    /// <summary>Whether an item of the root member <paramref name="arrayName"/> has <paramref name="field"/> equal to <paramref name="value"/>.</summary>
    public static bool ArrayContainsValue(string json, string arrayName, string field, string value)
    {
        using var document = JsonDocument.Parse(json, ReadOptions);
        return document.RootElement.TryGetProperty(arrayName, out var array)
            && array.ValueKind == JsonValueKind.Array
            && array.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty(field, out var found)
                && found.ValueKind == JsonValueKind.String
                && found.GetString() == value);
    }

    /// <summary>Appends <paramref name="item"/> (JSON text) to the array in the root member <paramref name="arrayName"/>.</summary>
    public static string AppendToArray(string json, string arrayName, string item)
    {
        var root = new Scanner(json).ParseRoot();
        var member = root.Members.LastOrDefault(m => m.Name == arrayName)
            ?? throw new InvalidOperationException($"The file has no root member \"{arrayName}\".");
        if (member.Value is not Container { IsArray: true } array)
            throw new InvalidOperationException($"\"{arrayName}\" is not an array.");
        return Insert(json, array, member.Start, item);
    }

    /// <summary>Adds the member <paramref name="name"/> with <paramref name="value"/> (JSON text) at the end of the root object.</summary>
    public static string AddProperty(string json, string name, string value)
    {
        var root = new Scanner(json).ParseRoot();
        return Insert(json, root, root.Start, $"{JsonSerializer.Serialize(name)}: {value}");
    }

    /// <summary>
    /// Inserts <paramref name="text"/> as the container's last element. A single-line container stays on one line; a
    /// multi-line one gets a line of its own, indented like the element before it.
    /// </summary>
    private static string Insert(string json, Container container, int ownerStart, string text)
    {
        var newline = NewLine(json);
        if (container.Elements.Count == 0)
        {
            var outer = IndentOfLine(json, ownerStart);
            var inner = outer + (outer.Length > 0 ? outer : "  ");
            return json[..(container.Open + 1)] + newline + inner + text + newline + outer + json[container.Close..];
        }

        var last = container.Elements[^1];
        var multiLine = json.AsSpan(container.Open, last.Start - container.Open).Contains('\n');
        var separator = multiLine ? newline + IndentOfLine(json, last.Start) : " ";
        // A trailing comma after the last element is kept: the new element goes after it.
        return container.TrailingComma is { } comma
            ? json[..(comma + 1)] + separator + text + json[(comma + 1)..]
            : json[..last.End] + "," + separator + text + json[last.End..];
    }

    /// <summary>The file's own line ending, so an edit never mixes CRLF into an LF file or the reverse.</summary>
    internal static string NewLine(string text) => text.Contains("\r\n") ? "\r\n" : "\n";

    private static string IndentOfLine(string json, int position)
    {
        var lineStart = json.LastIndexOf('\n', Math.Max(0, position - 1)) + 1;
        var end = lineStart;
        while (end < json.Length && json[end] is ' ' or '\t') end++;
        return json[lineStart..end];
    }

    private abstract record Value(int Start, int End);

    private sealed record Scalar(int Start, int End) : Value(Start, End);

    private sealed record Member(string Name, int Start, Value Value) : Value(Start, Value.End);

    private sealed record Container(int Start, int End, bool IsArray, int Open, int Close, IReadOnlyList<Value> Elements, int? TrailingComma)
        : Value(Start, End)
    {
        public IEnumerable<Member> Members => Elements.OfType<Member>();
    }

    /// <summary>A recursive-descent scan that records where each value starts and ends; it builds no values.</summary>
    private sealed class Scanner
    {
        private readonly string json;
        private int position;

        public Scanner(string json) => this.json = json;

        public Container ParseRoot()
        {
            SkipTrivia();
            if (Peek() != '{') throw Error("The file's root is not an object.");
            var root = (Container)ParseValue();
            SkipTrivia();
            if (position != json.Length) throw Error("Unexpected text after the root object.");
            return root;
        }

        private Value ParseValue()
        {
            SkipTrivia();
            var start = position;
            switch (Peek())
            {
                case '{': return ParseContainer(start, isArray: false);
                case '[': return ParseContainer(start, isArray: true);
                case '"': SkipString(); return new Scalar(start, position);
                default:
                    while (position < json.Length && json[position] is not (',' or '}' or ']' or ' ' or '\t' or '\r' or '\n' or '/'))
                        position++;
                    if (position == start) throw Error("A value was expected.");
                    return new Scalar(start, position);
            }
        }

        private Container ParseContainer(int start, bool isArray)
        {
            var close = isArray ? ']' : '}';
            position++;
            var elements = new List<Value>();
            int? trailingComma = null;
            while (true)
            {
                SkipTrivia();
                if (Peek() == close) break;
                trailingComma = null;
                if (isArray)
                {
                    elements.Add(ParseValue());
                }
                else
                {
                    var memberStart = position;
                    if (Peek() != '"') throw Error("A member name was expected.");
                    SkipString();
                    var name = JsonSerializer.Deserialize<string>(json[memberStart..position])!;
                    SkipTrivia();
                    if (Peek() != ':') throw Error("':' was expected.");
                    position++;
                    elements.Add(new Member(name, memberStart, ParseValue()));
                }
                SkipTrivia();
                if (Peek() == ',')
                {
                    trailingComma = position;
                    position++;
                    continue;
                }
                if (Peek() != close) throw Error($"',' or '{close}' was expected.");
            }
            var closeAt = position;
            position++;
            return new Container(start, position, isArray, start, closeAt, elements, trailingComma);
        }

        private void SkipString()
        {
            position++;
            while (position < json.Length && json[position] != '"')
                position += json[position] == '\\' ? 2 : 1;
            if (position >= json.Length) throw Error("A string is not closed.");
            position++;
        }

        private void SkipTrivia()
        {
            while (position < json.Length)
            {
                if (char.IsWhiteSpace(json[position])) position++;
                else if (json.AsSpan(position).StartsWith("//"))
                {
                    var end = json.IndexOf('\n', position);
                    position = end < 0 ? json.Length : end + 1;
                }
                else if (json.AsSpan(position).StartsWith("/*"))
                {
                    var end = json.IndexOf("*/", position + 2, StringComparison.Ordinal);
                    if (end < 0) throw Error("A comment is not closed.");
                    position = end + 2;
                }
                else break;
            }
        }

        private char Peek() => position < json.Length ? json[position] : '\0';

        private InvalidOperationException Error(string message)
        {
            var line = json.AsSpan(0, Math.Min(position, json.Length)).Count('\n') + 1;
            return new InvalidOperationException($"{message} (line {line})");
        }
    }
}
