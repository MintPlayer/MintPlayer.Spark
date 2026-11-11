namespace CodeCoverage.Forge;

/// <summary>
/// How a person asks for a promotion of release lines (segment P, <c>versioning.lines.current: promote</c>): a comment whose
/// first line is <c>/release [all | &lt;line&gt; …] [&lt;sha&gt;] [&lt;PackageId&gt;@&lt;version&gt; …]</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>No line: the promote lines whose ecosystem changed since their last promotion (P4). <c>all</c>: every promote line.
/// A line is what <see cref="BackportCommand.IsLine"/> accepts (<c>dotnet-11.x</c>, a branch name); the recipient resolves it.</item>
/// <item>No sha: the newest green default-branch commit (P2). A sha is 7 to 40 hex digits.</item>
/// <item><c>Pkg@1.2.3</c> names a package's next version (P5); a scoped npm id splits at its last <c>@</c>
/// (<c>@scope/pkg@22.1.0</c>). Whether the version is allowed is the recipient's to check.</item>
/// </list>
/// Anything else (an option, a malformed token, <c>all</c> next to a line, two shas, too many tokens) makes it no command,
/// so a typo never promotes something the person did not ask for.
/// </remarks>
public static class ReleaseCommand
{
    public const string CommentCommand = "/release";
    public const string AllLines = "all";

    /// <summary>The most lines or versions one request names; more is not a command.</summary>
    public const int MaxLines = 10;
    public const int MaxVersions = 20;
    public const int MaxPackageIdLength = 214;
    public const int MaxVersionLength = 64;

    public static ReleaseCommentRequest? Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        var first = body.TrimStart().Split('\n', 2)[0].TrimEnd('\r').Trim();
        var parts = first.Split((char[])[' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !parts[0].Equals(CommentCommand, StringComparison.OrdinalIgnoreCase))
            return null;

        var lines = new List<string>();
        var versions = new List<ReleaseVersionRequest>();
        string? sha = null;
        var all = false;
        foreach (var token in parts[1..].Select(p => p.Trim('`')))
        {
            if (token.Length == 0 || token.StartsWith('-'))
                return null;
            if (token.Equals(AllLines, StringComparison.OrdinalIgnoreCase))
            {
                if (all) return null;
                all = true;
            }
            else if (IsSha(token))
            {
                if (sha is not null) return null;
                sha = token.ToLowerInvariant();
            }
            else if (Version(token) is { } version)
            {
                if (versions.Any(v => v.PackageId.Equals(version.PackageId, StringComparison.OrdinalIgnoreCase))) return null;
                versions.Add(version);
            }
            else if (BackportCommand.IsLine(token))
            {
                if (!lines.Contains(token, StringComparer.OrdinalIgnoreCase)) lines.Add(token);
            }
            else
                return null;
        }
        if ((all && lines.Count > 0) || lines.Count > MaxLines || versions.Count > MaxVersions)
            return null;
        return new ReleaseCommentRequest(lines, all, sha, versions);
    }

    /// <summary>A commit id as a person writes one: 7 to 40 hex digits.</summary>
    public static bool IsSha(string? token)
        => token is { Length: >= 7 and <= 40 } && token.All(char.IsAsciiHexDigit);

    /// <summary><c>Pkg@1.2.3</c> (or <c>@scope/pkg@1.2.3</c>); null when the token is not one.</summary>
    public static ReleaseVersionRequest? Version(string token)
    {
        var at = token.LastIndexOf('@');
        if (at <= 0) return null;
        var id = token[..at];
        var version = token[(at + 1)..];
        return IsPackageId(id) && IsVersion(version) ? new ReleaseVersionRequest(id, version) : null;
    }

    /// <summary>A NuGet id or an npm name, scoped or not: letters, digits and <c>. _ -</c>, with one optional <c>@scope/</c>.</summary>
    public static bool IsPackageId(string id)
    {
        if (id.Length is 0 or > MaxPackageIdLength) return false;
        var name = id;
        if (id.StartsWith('@'))
        {
            var slash = id.IndexOf('/');
            if (slash <= 1 || slash == id.Length - 1 || !Simple(id[1..slash])) return false;
            name = id[(slash + 1)..];
        }
        return Simple(name);

        static bool Simple(string part) => part.Length > 0 && char.IsAsciiLetterOrDigit(part[0])
            && part.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
    }

    /// <summary>
    /// The shape of a version (<c>major.minor.patch</c>, an optional <c>-prerelease</c>); whether it is allowed (platform
    /// major, higher than the last tag, not on the registry) is checked later, P5.
    /// </summary>
    public static bool IsVersion(string version)
    {
        if (version.Length is 0 or > MaxVersionLength) return false;
        var dash = version.IndexOf('-');
        var core = dash < 0 ? version : version[..dash];
        var numbers = core.Split('.');
        if (numbers.Length != 3 || numbers.Any(n => n.Length is 0 or > 9 || !n.All(char.IsAsciiDigit) || (n.Length > 1 && n[0] == '0')))
            return false;
        if (dash < 0) return true;
        var pre = version[(dash + 1)..];
        return pre.Length > 0 && pre.Split('.').All(p => p.Length > 0 && p.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
    }
}

/// <summary>A parsed <c>/release</c> comment (segment P).</summary>
/// <param name="Lines">The lines named; empty means the changed ones (P4), unless <paramref name="All"/>.</param>
/// <param name="Sha">The default-branch commit to promote to; null means the newest green one (P2).</param>
/// <param name="Versions">The next versions named for packages (P5).</param>
public sealed record ReleaseCommentRequest(IReadOnlyList<string> Lines, bool All, string? Sha, IReadOnlyList<ReleaseVersionRequest> Versions);

/// <summary><c>Pkg@1.2.3</c> in a <c>/release</c> comment.</summary>
public sealed record ReleaseVersionRequest(string PackageId, string Version);
