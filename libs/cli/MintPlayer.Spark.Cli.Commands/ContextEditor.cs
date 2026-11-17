using System.Text.RegularExpressions;

namespace MintPlayer.Spark.Cli;

/// <summary>
/// Adds an entity root to the app's <c>SparkContext</c> subclass, the first step the spark-entity item's doc comment
/// spells out. A text edit: one line goes in after the last <c>IQueryable</c> root, and nothing else moves.
/// </summary>
internal static partial class ContextEditor
{
    [GeneratedRegex(@"^[^/\r\n]*\bclass\s+\w+\s*:\s*(?:[\w.]+\.)?SparkContext\b", RegexOptions.Multiline)]
    private static partial Regex ContextDeclaration();

    [GeneratedRegex(@"^([ \t]*)public\s+IQueryable<", RegexOptions.Multiline)]
    private static partial Regex Root();

    /// <summary>Whether the source declares a class that derives from <c>SparkContext</c>.</summary>
    public static bool IsContext(string source) => ContextDeclaration().IsMatch(source);

    /// <summary>Adds <c>public IQueryable&lt;Entity&gt; Plural =&gt; Session.Query&lt;Entity&gt;();</c> to the context class.</summary>
    public static string AddRoot(string source, string entity, string plural)
    {
        var declaration = ContextDeclaration().Match(source);
        if (!declaration.Success)
            throw new InvalidOperationException("The file declares no class deriving from SparkContext.");

        var open = source.IndexOf('{', declaration.Index + declaration.Length);
        var close = MatchingBrace(source, open);
        var body = source[(open + 1)..close];
        if (Regex.IsMatch(body, $@"\b{Regex.Escape(plural)}\s*(=>|\{{|;)"))
            throw new InvalidOperationException($"The context already has a member named {plural}.");

        var newline = JsonText.NewLine(source);
        var roots = Root().Matches(body);
        string indent;
        int insertAt;
        if (roots.Count > 0)
        {
            var last = roots[^1];
            indent = last.Groups[1].Value;
            var lineEnd = source.IndexOf('\n', open + 1 + last.Index);
            insertAt = lineEnd < 0 || lineEnd > close ? close : lineEnd + 1;
        }
        else
        {
            indent = LeadingWhitespace(source, declaration.Index) + "    ";
            insertAt = source.LastIndexOf('\n', close - 1) + 1;
            if (insertAt <= open) insertAt = close;
        }

        var line = $"{indent}public IQueryable<{entity}> {plural} => Session.Query<{entity}>();{newline}";
        return source[..insertAt] + line + source[insertAt..];
    }

    private static int MatchingBrace(string source, int open)
    {
        if (open < 0) throw new InvalidOperationException("The SparkContext class has no body.");
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return i;
        }
        throw new InvalidOperationException("The SparkContext class body is not closed.");
    }

    private static string LeadingWhitespace(string source, int position)
    {
        var lineStart = source.LastIndexOf('\n', Math.Max(0, position - 1)) + 1;
        var end = lineStart;
        while (end < source.Length && source[end] is ' ' or '\t') end++;
        return source[lineStart..end];
    }
}
