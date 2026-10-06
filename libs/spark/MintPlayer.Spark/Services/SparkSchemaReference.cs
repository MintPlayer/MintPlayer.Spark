using System.Text;
using System.Text.RegularExpressions;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Keeps the <c>"$schema"</c> line of the six hand-edited App_Data files pointing at the schema
/// revision this package was built against (#264, G-Q17):
/// <c>https://schemas.spark.mintplayer.com/v{n}/&lt;file&gt;.schema.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// A missing <c>$schema</c> is added; on a hosted URL only the <c>v{n}</c> segment is rewritten; any
/// other value (a relative path, as this repository's own apps use, G-Q20, or a URL of the
/// developer's choosing) is left alone.
/// </para>
/// <para>
/// ⚠️ The edit is textual, never a parse-and-serialize round trip: the files are hand-written, and
/// their formatting, key order and <c>_comment</c>s must survive byte-for-byte. Only the one line
/// changes, and a file is rewritten only when that line did.
/// </para>
/// <para>
/// With <see cref="SparkSchemaRevision.Current"/> at 0 (a build that knows of no published revision)
/// nothing is written: a URL naming a revision that does not exist would only 404 in the editor.
/// </para>
/// </remarks>
internal static partial class SparkSchemaReference
{
    internal const string HostedRoot = "https://schemas.spark.mintplayer.com/";

    /// <summary>The App_Data files (relative to App_Data, <c>*</c> = every file in the folder) and their schema's name.</summary>
    internal static readonly (string Pattern, string Schema)[] Files =
    [
        ("Model/*.json", "model"),
        ("security.json", "security"),
        ("programUnits.json", "programUnits"),
        ("translations.json", "translations"),
        ("culture.json", "culture"),
        ("actions.json", "actions"),
    ];

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    [GeneratedRegex(@"^https://schemas\.spark\.mintplayer\.com/v[0-9]+/", RegexOptions.CultureInvariant)]
    private static partial Regex HostedUrl();

    internal static string HostedUrlFor(string schema, int revision)
        => $"{HostedRoot}v{revision}/{schema}.schema.json";

    /// <summary>Applies <see cref="Apply"/> to every file of the six kinds under <paramref name="contentRootPath"/>. Returns the files changed.</summary>
    internal static IReadOnlyList<string> ApplyAll(string contentRootPath, int revision)
    {
        var changed = new List<string>();
        if (revision <= 0)
            return changed;

        var appData = SparkAppData.Directory(contentRootPath);
        foreach (var (pattern, schema) in Files)
        {
            var directory = Path.Combine(appData, Path.GetDirectoryName(pattern) ?? string.Empty);
            if (!Directory.Exists(directory))
                continue;

            foreach (var path in Directory.GetFiles(directory, Path.GetFileName(pattern)).OrderBy(p => p, StringComparer.Ordinal))
            {
                var bytes = File.ReadAllBytes(path);
                var hasBom = bytes.AsSpan().StartsWith(Utf8Bom);
                var text = Encoding.UTF8.GetString(bytes, hasBom ? Utf8Bom.Length : 0, bytes.Length - (hasBom ? Utf8Bom.Length : 0));

                var updated = Apply(text, schema, revision);
                if (ReferenceEquals(updated, text))
                    continue;

                var encoded = Encoding.UTF8.GetBytes(updated);
                File.WriteAllBytes(path, hasBom ? [.. Utf8Bom, .. encoded] : encoded);
                changed.Add(path);
            }
        }

        return changed;
    }

    /// <summary>
    /// <paramref name="text"/> with its root <c>$schema</c> pointing at <paramref name="revision"/>;
    /// the same instance when nothing needed to change (or the text is not a JSON object).
    /// </summary>
    internal static string Apply(string text, string schema, int revision)
    {
        if (revision <= 0 || Scan(text) is not { } scan)
            return text;

        if (scan.Value is { } value)
        {
            if (!HostedUrl().IsMatch(text.AsSpan(value.Start, value.Length)))
                return text;

            var current = text.Substring(value.Start, value.Length);
            var rewritten = HostedUrl().Replace(current, $"{HostedRoot}v{revision}/", 1);
            return rewritten == current
                ? text
                : string.Concat(text.AsSpan(0, value.Start), rewritten, text.AsSpan(value.Start + value.Length));
        }

        return Insert(text, scan.RootOpen, HostedUrlFor(schema, revision));
    }

    /// <summary>
    /// The model synchronizer regenerates a model file from its object model, which has no
    /// <c>$schema</c>; this carries the value the file on disk had over into the new text.
    /// </summary>
    internal static string CarryOver(string path, string generated)
    {
        if (!File.Exists(path) || Read(File.ReadAllText(path)) is not { } existing)
            return generated;
        if (Scan(generated) is not { Value: null } scan)
            return generated;
        return Insert(generated, scan.RootOpen, existing);
    }

    /// <summary>The root <c>$schema</c> value, or null when there is none.</summary>
    internal static string? Read(string text)
        => Scan(text) is { Value: { } value } ? text.Substring(value.Start, value.Length) : null;

    /// <summary>Inserts <c>"$schema": "url"</c> as the root object's first property, on a line of its own when the file has one property per line.</summary>
    private static string Insert(string text, int rootOpen, string url)
    {
        var property = $"\"$schema\": \"{url}\"";
        var first = rootOpen + 1;
        while (first < text.Length && char.IsWhiteSpace(text[first]))
            first++;

        var between = text.AsSpan(rootOpen + 1, first - rootOpen - 1);
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        if (first < text.Length && text[first] == '}')
        {
            // An empty object: there is no neighbour to take the layout from.
            return string.Concat(text.AsSpan(0, rootOpen + 1), $"{newLine}  {property}{newLine}", text.AsSpan(first));
        }

        var lineBreak = between.LastIndexOf('\n');
        if (lineBreak < 0)
        {
            // `{ "a": 1, ... }` on one line: keep it on that line.
            return string.Concat(text.AsSpan(0, first), property, ", ", text.AsSpan(first));
        }

        // The first property's own line: same indentation, inserted above it.
        var lineStart = rootOpen + 1 + lineBreak + 1;
        var indent = text.AsSpan(lineStart, first - lineStart);
        return string.Concat(text.AsSpan(0, lineStart), string.Concat(indent, property, ",", newLine), text.AsSpan(lineStart));
    }

    private readonly record struct Span(int Start, int Length);

    private readonly record struct ScanResult(int RootOpen, Span? Value);

    /// <summary>
    /// Finds the root object's opening brace and, at depth 1, the string value of a <c>$schema</c>
    /// property. Strings and comments (the loaders accept <c>//</c> and <c>/* */</c>) are skipped,
    /// so a brace or a <c>"$schema"</c> inside them is not mistaken for structure.
    /// </summary>
    private static ScanResult? Scan(string text)
    {
        var rootOpen = -1;
        var depth = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                i = text.IndexOf('\n', i) is var end and >= 0 ? end + 1 : text.Length;
                continue;
            }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i = text.IndexOf("*/", i + 2, StringComparison.Ordinal) is var end and >= 0 ? end + 2 : text.Length;
                continue;
            }
            if (c == '"')
            {
                var close = StringEnd(text, i);
                if (close < 0)
                    return null;

                if (depth == 1 && text.AsSpan(i + 1, close - i - 1).SequenceEqual("$schema"))
                {
                    var j = close + 1;
                    while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
                    if (j < text.Length && text[j] == ':')
                    {
                        j++;
                        while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
                        if (j < text.Length && text[j] == '"' && StringEnd(text, j) is var valueClose and >= 0)
                            return new ScanResult(rootOpen, new Span(j + 1, valueClose - j - 1));
                    }
                }

                i = close + 1;
                continue;
            }
            if (c is '{' or '[')
            {
                if (depth == 0)
                {
                    if (c != '{')
                        return null;
                    rootOpen = i;
                }
                depth++;
            }
            else if (c is '}' or ']')
            {
                depth--;
                if (depth == 0)
                    break;
            }
            i++;
        }

        return rootOpen < 0 ? null : new ScanResult(rootOpen, null);
    }

    /// <summary>The index of the quote closing the string that opens at <paramref name="open"/>, or -1.</summary>
    private static int StringEnd(string text, int open)
    {
        for (var i = open + 1; i < text.Length; i++)
        {
            if (text[i] == '\\') { i++; continue; }
            if (text[i] == '"') return i;
        }
        return -1;
    }
}
