namespace CodeCoverage.Forge;

/// <summary>
/// How a person asks for a backport (#153 M10, PRD D10): a label <c>backport: 8.x</c> (or <c>backport 8.x</c>) or a comment
/// whose first line is <c>/backport 8.x 9.x</c>. A line is a major, <c>N.x</c> or a branch name; which branch it names
/// is the recipient's to resolve against <c>versioning.releaseBranches</c>. A normaliser raises
/// <see cref="BackportRequested"/> only for what this accepts.
/// </summary>
public static class BackportCommand
{
    public const string LabelPrefix = "backport";
    public const string CommentCommand = "/backport";

    /// <summary>The most lines one request names; more is not a command.</summary>
    public const int MaxLines = 10;

    public const int MaxLineLength = 100;

    /// <summary>The line a label names, or null when the label is not a backport label.</summary>
    public static string? FromLabel(string? label)
    {
        // "backport" must be followed by ':' or a space, so a label like "backported" is not one.
        if (label is null || label.Length <= LabelPrefix.Length || !label.StartsWith(LabelPrefix, StringComparison.OrdinalIgnoreCase)
            || label[LabelPrefix.Length] is not (':' or ' '))
            return null;
        var rest = label[LabelPrefix.Length..].TrimStart(':', ' ').Trim();
        return IsLine(rest) ? rest : null;
    }

    /// <summary>The lines every backport label among <paramref name="labels"/> names, in order, without duplicates.</summary>
    public static IReadOnlyList<string> FromLabels(IEnumerable<string?> labels)
        => labels.Select(FromLabel).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxLines).ToList();

    /// <summary>The lines a <c>/backport</c> comment names; null when the comment is not one, or names none or too many.</summary>
    public static IReadOnlyList<string>? FromComment(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        var line = body.TrimStart().Split('\n', 2)[0].TrimEnd('\r').Trim();
        var parts = line.Split((char[])[' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !parts[0].Equals(CommentCommand, StringComparison.OrdinalIgnoreCase))
            return null;
        var lines = parts[1..].Select(p => p.Trim('`')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return lines.Count <= MaxLines && lines.All(IsLine) ? lines : null;
    }

    /// <summary>A line token: letters, digits and <c>. _ / -</c>, so a major, <c>N.x</c> or a branch name.</summary>
    public static bool IsLine(string? token)
        => token is { Length: > 0 and <= MaxLineLength } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '/' or '-');

    /// <summary>The major a line names: <c>8</c> or <c>8.x</c> (case-insensitively); null for a branch name.</summary>
    public static int? Major(string line)
    {
        var digits = line.EndsWith(".x", StringComparison.OrdinalIgnoreCase) ? line[..^2] : line;
        return digits.Length is > 0 and <= 6 && digits.All(char.IsAsciiDigit) ? int.Parse(digits, System.Globalization.CultureInfo.InvariantCulture) : null;
    }
}
