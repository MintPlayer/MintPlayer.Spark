using System.Text;
using System.Text.RegularExpressions;

namespace MintPlayer.Spark;

/// <summary>
/// Reads and rewrites the <b>rendered</b> C# map of a RavenDB index — the string in
/// <c>IndexDefinition.Maps</c> — for the default-index work (<c>docs/default_index_PRD.md</c>, D4 and SP1/SP4):
/// which collection a map reads (<see cref="CollectionOf"/>), which fields it emits
/// (<see cref="EmittedFields"/>), and injecting the collection discriminator
/// (<see cref="InjectCollectionField"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a string scanner is enough.</b> RavenDB normalizes a C# map before anyone sees it: query syntax
/// becomes a method chain on <c>docs.&lt;Collection&gt;</c>, each <c>let</c> becomes a chained
/// <c>Select</c>, and a typed result (<c>new Entry { … }</c>) becomes an anonymous <c>new { … }</c> with
/// every member named (<c>Name = c.Name</c>). So every map has the shape
/// <c>docs.Cars[.Where(…)][.Select(…)]….Select(c =&gt; new { … })</c> — or <c>.SelectMany(…, (c, x) =&gt; new { … })</c>
/// for a fan-out — and its emitted fields are the members of that final anonymous object (SP1: plain objects,
/// <c>let</c>, <c>where</c> before or after <c>let</c>, nested <c>new { }</c>, string literals containing
/// <c>})</c>, fan-out, method syntax and typed results are all read correctly, and the server compiles the
/// rewritten maps).
/// </para>
/// <para>
/// The scanner tracks bracket depth and skips string and character literals (regular and verbatim), so a
/// <c>"})"</c> inside a literal never closes anything. What it cannot read it refuses rather than guesses:
/// a final call that is not <c>Select</c>/<c>SelectMany</c>, a result selector that is not a lambda returning
/// <c>new { … }</c> — notably a conditional body <c>c.X ? new { … } : new { … }</c>. JavaScript maps are not
/// C# and are not read at all (a JS index cannot be a multi-map default, PRD §7 SP1).
/// </para>
/// </remarks>
public static partial class SparkMapScanner
{
    [GeneratedRegex(@"\A\s*docs\s*(?:\.\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\b|\[\s*(?<literal>@?""(?:[^""\\]|\\.|"""")*"")\s*\])")]
    private static partial Regex CollectionPrefix();

    [GeneratedRegex(@"\A(?<name>@?[A-Za-z_][A-Za-z0-9_]*)\s*=(?![=>])")]
    private static partial Regex NamedMember();

    [GeneratedRegex(@"(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\z")]
    private static partial Regex TrailingIdentifier();

    [GeneratedRegex(@"\Anew\s*\{")]
    private static partial Regex AnonymousObject();

    /// <summary>
    /// The collection a rendered map reads (<c>docs.Cars…</c> ⇒ <c>Cars</c>), or <see langword="null"/>
    /// when the map does not start at <c>docs</c> (a JavaScript map, a hand-built definition).
    /// </summary>
    public static string? CollectionOf(string map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var match = CollectionPrefix().Match(map);
        if (!match.Success) return null;
        return match.Groups["name"].Success ? match.Groups["name"].Value : DecodeLiteral(match.Groups["literal"].Value);
    }

    /// <summary>
    /// The names of the fields a rendered map emits — the members of its final anonymous object — or
    /// <see langword="null"/> when the map's shape cannot be read (see the class remarks).
    /// </summary>
    /// <remarks>
    /// This is a viewless default index's query surface (SP4): RavenDB answers a sort or filter on a field the
    /// maps never emit with "The field '…' is not indexed", so the synchronizer and the dev gate treat only
    /// these names as sortable and filterable.
    /// </remarks>
    public static IReadOnlySet<string>? EmittedFields(string map)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (!TryLocateResultObject(map, out var braceOpen, out var braceClose, out _)) return null;

        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in SplitTopLevel(map, braceOpen + 1, braceClose))
        {
            var text = member.Trim();
            if (text.Length == 0) continue;

            // `Name = expr` is what RavenDB renders; a bare member access `c.Name` (projection initializer)
            // takes the accessed member's name, as the C# compiler does.
            var named = NamedMember().Match(text);
            var name = named.Success ? named.Groups["name"].Value : TrailingIdentifier().Match(text).Groups["name"].Value;
            if (name.Length == 0) return null;
            fields.Add(name.TrimStart('@'));
        }
        return fields;
    }

    /// <summary>
    /// Whether a rendered map already emits <see cref="SparkCollectionRow.FieldName"/> anywhere — an author
    /// who writes the discriminator by hand (for instance in both branches of a conditional body, which the
    /// rewrite refuses) is left alone.
    /// </summary>
    public static bool EmitsCollectionField(string map)
        => Regex.IsMatch(map, @"\b" + SparkCollectionRow.FieldName + @"\s*=(?![=>])");

    /// <summary>
    /// Returns the map with <c>SparkCollection = "&lt;Collection&gt;"</c> added as the first member of its final
    /// anonymous object. A map that already emits the field is returned unchanged, so the rewrite is
    /// idempotent.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// The map's shape cannot be read (see the class remarks). The message names the fix: emit the field
    /// yourself.
    /// </exception>
    public static string InjectCollectionField(string map)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (EmitsCollectionField(map)) return map;

        var prefix = CollectionPrefix().Match(map);
        if (!prefix.Success)
            throw Refuse(map, "it does not read from docs.<Collection>");

        if (!TryLocateResultObject(map, out var braceOpen, out var braceClose, out var reason))
            throw Refuse(map, reason!);

        // Reuse the collection exactly as the map spells it: an identifier is safe as-is, and a quoted
        // collection keeps its own (already valid) C# literal, so nothing is ever escaped by hand here.
        var value = prefix.Groups["name"].Success ? $"\"{prefix.Groups["name"].Value}\"" : prefix.Groups["literal"].Value;
        var isEmpty = map.AsSpan(braceOpen + 1, braceClose - braceOpen - 1).Trim().Length == 0;
        // No newline: the injected text is the same on every OS, so the deployed definition — which RavenDB
        // compares to decide whether to re-index — does not depend on where the app was started.
        var injection = $" {SparkCollectionRow.FieldName} = {value}{(isEmpty ? " " : ",")}";
        return map.Insert(braceOpen + 1, injection);
    }

    /// <summary>
    /// Finds the braces of the final result selector's <c>new { … }</c>: the last argument of the last
    /// depth-0 call, which must be <c>Select</c> or <c>SelectMany</c> and end the map.
    /// </summary>
    private static bool TryLocateResultObject(string map, out int braceOpen, out int braceClose, out string? reason)
    {
        braceOpen = braceClose = -1;

        var depth = 0;
        var lastCallOpen = -1;
        string? lastCallName = null;
        for (var i = 0; i < map.Length;)
        {
            var c = map[i];
            if (c is '"' or '\'')
            {
                i = SkipLiteral(map, i);
                if (i < 0) { reason = "it has an unterminated literal"; return false; }
                continue;
            }

            if (c is '(' or '{' or '[')
            {
                if (c == '(' && depth == 0)
                {
                    var j = i - 1;
                    while (j >= 0 && char.IsWhiteSpace(map[j])) j--;
                    var end = j + 1;
                    while (j >= 0 && (char.IsLetterOrDigit(map[j]) || map[j] == '_')) j--;
                    lastCallName = map[(j + 1)..end];
                    lastCallOpen = i;
                }
                depth++;
            }
            else if (c is ')' or '}' or ']')
            {
                depth--;
                if (depth < 0) { reason = "its brackets do not balance"; return false; }
            }
            i++;
        }

        if (depth != 0) { reason = "its brackets do not balance"; return false; }
        if (lastCallOpen < 0 || lastCallName is not ("Select" or "SelectMany"))
        {
            reason = $"its final call is '{lastCallName ?? "(none)"}', not Select or SelectMany";
            return false;
        }

        var callClose = MatchClose(map, lastCallOpen);
        if (map.AsSpan(callClose + 1).Trim().Length != 0)
        {
            reason = "text follows its final Select";
            return false;
        }

        // The result selector is the call's last top-level argument (SelectMany's second, Select's only).
        var argStart = LastTopLevelArgumentStart(map, lastCallOpen + 1, callClose);
        var arrow = map.IndexOf("=>", argStart, callClose - argStart, StringComparison.Ordinal);
        if (arrow < 0)
        {
            reason = "its result selector is not a lambda";
            return false;
        }

        var bodyStart = arrow + 2;
        while (bodyStart < callClose && char.IsWhiteSpace(map[bodyStart])) bodyStart++;
        var anonymous = AnonymousObject().Match(map[bodyStart..callClose]);
        if (!anonymous.Success)
        {
            reason = "its result selector does not return 'new { … }' (a conditional body, for instance)";
            return false;
        }

        braceOpen = bodyStart + anonymous.Length - 1;
        braceClose = MatchClose(map, braceOpen);

        // `new { … }.Something` or `new { … } ?? x` is not a plain object either.
        if (map.AsSpan(braceClose + 1, callClose - braceClose - 1).Trim().Length != 0)
        {
            reason = "its result selector does more than return 'new { … }'";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>The index just past a string or character literal starting at <paramref name="start"/>; -1 if unterminated.</summary>
    private static int SkipLiteral(string s, int start)
    {
        var quote = s[start];
        var verbatim = quote == '"' && start > 0 && s[start - 1] == '@';
        for (var i = start + 1; i < s.Length; i++)
        {
            if (!verbatim && s[i] == '\\') { i++; continue; }
            if (s[i] != quote) continue;
            if (verbatim && i + 1 < s.Length && s[i + 1] == quote) { i++; continue; }
            return i + 1;
        }
        return -1;
    }

    /// <summary>The closing bracket matching the one at <paramref name="open"/>.</summary>
    private static int MatchClose(string s, int open)
    {
        var depth = 0;
        for (var i = open; i < s.Length;)
        {
            var c = s[i];
            if (c is '"' or '\'') { i = SkipLiteral(s, i); if (i < 0) break; continue; }
            if (c is '(' or '{' or '[') depth++;
            else if (c is ')' or '}' or ']' && --depth == 0) return i;
            i++;
        }
        throw new NotSupportedException("Unbalanced brackets in a rendered index map.");
    }

    private static int LastTopLevelArgumentStart(string s, int from, int to)
    {
        var start = from;
        foreach (var (segmentStart, _) in TopLevelSegments(s, from, to)) start = segmentStart;
        return start;
    }

    private static IEnumerable<string> SplitTopLevel(string s, int from, int to)
        => TopLevelSegments(s, from, to).Select(seg => s[seg.Start..seg.End]);

    /// <summary>The comma-separated depth-0 segments of <c>s[from..to]</c>, skipping literals.</summary>
    private static IEnumerable<(int Start, int End)> TopLevelSegments(string s, int from, int to)
    {
        var depth = 0;
        var segmentStart = from;
        for (var i = from; i < to;)
        {
            var c = s[i];
            if (c is '"' or '\'') { i = SkipLiteral(s, i); if (i < 0) yield break; continue; }
            if (c is '(' or '{' or '[') depth++;
            else if (c is ')' or '}' or ']') depth--;
            else if (c == ',' && depth == 0)
            {
                yield return (segmentStart, i);
                segmentStart = i + 1;
            }
            i++;
        }
        yield return (segmentStart, to);
    }

    /// <summary>Decodes a C# string literal (regular or verbatim) to its value.</summary>
    private static string DecodeLiteral(string literal)
    {
        if (literal.StartsWith('@'))
            return literal[2..^1].Replace("\"\"", "\"", StringComparison.Ordinal);

        var body = literal[1..^1];
        var value = new StringBuilder(body.Length);
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] == '\\' && i + 1 < body.Length)
            {
                i++;
                value.Append(body[i] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', '0' => '\0', var other => other });
            }
            else
            {
                value.Append(body[i]);
            }
        }
        return value.ToString();
    }

    private static NotSupportedException Refuse(string map, string reason)
    {
        var head = map.Length > 80 ? map[..80] + "…" : map;
        return new NotSupportedException(
            $"Spark cannot add the '{SparkCollectionRow.FieldName}' discriminator to this multi-map default's map " +
            $"because {reason}: {head}. Emit it yourself in every result object of the map " +
            $"({SparkCollectionRow.FieldName} = \"<Collection>\") and Spark leaves the map alone.");
    }
}
