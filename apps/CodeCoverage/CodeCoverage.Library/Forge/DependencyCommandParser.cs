namespace CodeCoverage.Forge;

/// <summary>
/// The dependency updater's comment commands (§7.5, R6): the first word of a comment's first line, case-insensitively, and
/// the rest of that line as the argument. A normaliser raises <see cref="DependencyCommandIssued"/> only for what this
/// accepts; anything else is an ordinary comment.
/// </summary>
public static class DependencyCommandParser
{
    /// <summary>The longest argument kept (a group or package name); a longer one is not a command.</summary>
    public const int MaxArgumentLength = 200;

    public static (EDependencyCommand Command, string? Argument)? Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        var line = body.TrimStart().Split('\n', 2)[0].TrimEnd('\r').Trim();
        var parts = line.Split((char[])[' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;
        EDependencyCommand? command = parts[0].ToLowerInvariant() switch
        {
            "approve" => EDependencyCommand.Approve,
            "hold" => EDependencyCommand.Hold,
            "unhold" => EDependencyCommand.Unhold,
            "bisect" => EDependencyCommand.Bisect,
            "refresh" => EDependencyCommand.Refresh,
            _ => null,
        };
        if (command is not { } found)
            return null;
        var argument = parts.Length > 1 ? parts[1].Trim().Trim('`') : null;
        if (argument is { Length: 0 })
            argument = null;
        if (argument is { Length: > MaxArgumentLength } || argument?.Any(char.IsControl) == true)
            return null;
        // approve, hold and unhold name what they act on; bisect and refresh take nothing.
        return found switch
        {
            EDependencyCommand.Approve or EDependencyCommand.Hold or EDependencyCommand.Unhold when argument is null => null,
            EDependencyCommand.Bisect or EDependencyCommand.Refresh when argument is not null => null,
            _ => (found, argument),
        };
    }
}
