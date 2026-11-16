namespace CodeCoverage.Forge;

/// <summary>
/// The dependency updater's comment commands (§7.5, R6): the first word of a comment's first line, case-insensitively, with
/// its leading <c>/</c> (#660 G1: <c>/approve</c>, <c>/hold</c>, <c>/unhold</c>, <c>/bisect</c>, <c>/refresh</c>), and
/// the rest of that line as the argument. A normaliser raises <see cref="DependencyCommandIssued"/> for what this accepts,
/// a malformed known command included (#683); anything else is an ordinary comment.
/// </summary>
public static class DependencyCommandParser
{
    /// <summary>The longest argument kept (a group or package name); a longer one is not a command.</summary>
    public const int MaxArgumentLength = 200;

    /// <summary>#683: how each command is written, for the answer to a malformed one.</summary>
    public static string Usage(EDependencyCommand command) => command switch
    {
        EDependencyCommand.Approve => "/approve <group>",
        EDependencyCommand.Hold => "/hold <group or package>",
        EDependencyCommand.Unhold => "/unhold <group or package>",
        EDependencyCommand.Bisect => "/bisect",
        EDependencyCommand.Refresh => "/refresh",
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
    };

    /// <returns>Null for an ordinary comment, an unknown slash word included (it may be another bot's command). A known
    /// command with the wrong arguments (missing, unexpected, overlong, control characters) is <c>Malformed</c>, with no
    /// argument (#683): a writer gets its <see cref="Usage"/> back.</returns>
    public static (EDependencyCommand Command, string? Argument, bool Malformed)? Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        var line = body.TrimStart().Split('\n', 2)[0].TrimEnd('\r').Trim();
        var parts = line.Split((char[])[' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;
        // #660 G1: only the slash forms. A comment opening with a bare "Hold on…" or "Refresh my memory…" is a comment.
        EDependencyCommand? command = parts[0].ToLowerInvariant() switch
        {
            "/approve" => EDependencyCommand.Approve,
            "/hold" => EDependencyCommand.Hold,
            "/unhold" => EDependencyCommand.Unhold,
            "/bisect" => EDependencyCommand.Bisect,
            "/refresh" => EDependencyCommand.Refresh,
            _ => null,
        };
        if (command is not { } found)
            return null;
        var argument = parts.Length > 1 ? parts[1].Trim().Trim('`') : null;
        if (argument is { Length: 0 })
            argument = null;
        if (argument is { Length: > MaxArgumentLength } || argument?.Any(char.IsControl) == true)
            return (found, null, true);
        // approve, hold and unhold name what they act on; bisect and refresh take nothing.
        return found switch
        {
            EDependencyCommand.Approve or EDependencyCommand.Hold or EDependencyCommand.Unhold when argument is null => (found, null, true),
            EDependencyCommand.Bisect or EDependencyCommand.Refresh when argument is not null => (found, null, true),
            _ => (found, argument, false),
        };
    }
}
