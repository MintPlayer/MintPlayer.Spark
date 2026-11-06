using System.Text.RegularExpressions;

namespace CodeCoverage.Feedback;

/// <summary>
/// Adds a top-level section to a repository's <c>coverage.yml</c> the way every bot pull request does (#571 M10a,
/// coverage_backport_PRD §8.4 "M10"): in alphabetical order, as text, never re-serialised, so the owner's comments and
/// layout stay as they are.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where:</b> before the first top-level key that sorts after the new one (case-insensitively), and before the comment
/// lines directly above that key, which belong to it. A commented-out section (<c># dependencies:</c> followed by indented
/// commented lines, as the dependency onboarding writes it) counts as a key. Nothing sorts after it: it is appended. One
/// blank line separates the section from its neighbours.
/// </para>
/// <para>
/// <b>Why the order matters:</b> two additions give the same file in either order, so two bot pull requests that each add a
/// section agree on the result. A bot pull request is still replanned on the default branch's new head rather than merged,
/// which is what keeps it free of conflicts; the order keeps its diff the same across those rebuilds.
/// </para>
/// </remarks>
public static partial class CoverageYmlSections
{
    /// <summary>Whether <paramref name="yml"/> has <paramref name="key"/> as a top-level key (not indented, not commented out).</summary>
    public static bool Has(string? yml, string key)
        => yml is not null && Lines(yml).Any(line => KeyOf(line) == key);

    /// <summary>
    /// <paramref name="yml"/> with <paramref name="section"/> (its comment lines, <c>key:</c> and the indented body) added in
    /// order; <paramref name="yml"/> itself when it has <paramref name="key"/> already. A missing file becomes the section
    /// alone. The file keeps its line endings.
    /// </summary>
    public static string Add(string? yml, string key, string section)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(section);
        if (yml is not null && Has(yml, key))
            return yml;

        var newline = yml is not null && yml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var added = section.ReplaceLineEndings("\n").Trim('\n').Split('\n');
        if (string.IsNullOrWhiteSpace(yml))
            return string.Join(newline, added) + newline;

        var lines = Lines(yml);
        var at = lines.Count;
        for (var i = 0; i < lines.Count; i++)
        {
            if ((KeyOf(lines[i]) ?? CommentedKeyOf(lines, i)) is not { } other || string.Compare(other, key, StringComparison.OrdinalIgnoreCase) <= 0)
                continue;
            at = i;
            while (at > 0 && lines[at - 1].StartsWith('#'))
                at--;
            break;
        }

        var result = new List<string>(lines.Count + added.Length + 2);
        result.AddRange(lines.Take(at));
        if (result.Count > 0 && !string.IsNullOrWhiteSpace(result[^1]))
            result.Add(string.Empty);
        result.AddRange(added);
        if (at < lines.Count && !string.IsNullOrWhiteSpace(lines[at]))
            result.Add(string.Empty);
        result.AddRange(lines.Skip(at));
        return string.Join(newline, result) + newline;
    }

    /// <summary>
    /// <paramref name="yml"/> with <paramref name="text"/> (<c>key: value</c>, or <c>key:</c> and lines indented under it)
    /// added to the block section <paramref name="section"/>, at its keys' indent and in alphabetical order among them;
    /// <paramref name="yml"/> itself when the section has <paramref name="key"/> already. Null when the file has no such
    /// section, or it is not a block mapping (<c>versioning: { mode: server }</c>), which text editing cannot extend safely:
    /// the caller says what to add instead.
    /// </summary>
    public static string? AddKey(string yml, string section, string key, string text)
    {
        ArgumentNullException.ThrowIfNull(yml);
        ArgumentException.ThrowIfNullOrEmpty(key);
        var newline = yml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = Lines(yml);
        var start = lines.FindIndex(line => KeyOf(line) == section);
        if (start < 0 || !BlockHeader().IsMatch(lines[start]))
            return null;

        // The body runs to the next top-level key, less the comments and blank lines in front of it (they are that key's).
        var end = lines.FindIndex(start + 1, line => KeyOf(line) is not null);
        if (end < 0) end = lines.Count;
        while (end > start + 1 && (string.IsNullOrWhiteSpace(lines[end - 1]) || lines[end - 1].StartsWith('#')))
            end--;

        var children = new List<(int Index, int Indent, string Key)>();
        for (var i = start + 1; i < end; i++)
            if (ChildKey().Match(lines[i]) is { Success: true } match)
                children.Add((i, match.Groups[1].Length, match.Groups[2].Value));
        var indent = children.Count > 0 ? children.Min(c => c.Indent) : 2;
        children.RemoveAll(c => c.Indent != indent);
        if (children.Any(c => c.Key == key))
            return yml;

        var at = end;
        if (children.FirstOrDefault(c => string.Compare(c.Key, key, StringComparison.OrdinalIgnoreCase) > 0) is { Key: not null } next)
        {
            at = next.Index;
            while (at > start + 1 && lines[at - 1].TrimStart().StartsWith('#'))
                at--;
        }
        var pad = new string(' ', indent);
        var added = text.ReplaceLineEndings("\n").Trim('\n').Split('\n').Select(line => line.Length == 0 ? line : pad + line);
        lines.InsertRange(at, added);
        return string.Join(newline, lines) + newline;
    }

    /// <summary>The file's lines without their line endings, and without the empty one after a final newline.</summary>
    private static List<string> Lines(string yml)
    {
        var lines = yml.ReplaceLineEndings("\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static string? KeyOf(string line) => TopLevelKey().Match(line) is { Success: true } match ? match.Groups[1].Value : null;

    /// <summary>The key of a commented-out section: <c># key:</c> at the start of a line, followed by an indented commented line.</summary>
    private static string? CommentedKeyOf(List<string> lines, int i)
        => i + 1 < lines.Count && CommentedKey().Match(lines[i]) is { Success: true } match && CommentedBody().IsMatch(lines[i + 1])
            ? match.Groups[1].Value
            : null;

    [GeneratedRegex(@"^([A-Za-z_][A-Za-z0-9_-]*)\s*:(?:\s|$)")]
    private static partial Regex TopLevelKey();

    /// <summary>A section whose value is a block on the lines below: nothing after the colon but a comment.</summary>
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_-]*\s*:\s*(?:#.*)?$")]
    private static partial Regex BlockHeader();

    [GeneratedRegex(@"^( +)([A-Za-z_][A-Za-z0-9_-]*)\s*:(?:\s|$)")]
    private static partial Regex ChildKey();

    [GeneratedRegex(@"^# ?([A-Za-z_][A-Za-z0-9_-]*):\s*(?:#.*)?$")]
    private static partial Regex CommentedKey();

    [GeneratedRegex(@"^#\s{2,}\S")]
    private static partial Regex CommentedBody();
}
