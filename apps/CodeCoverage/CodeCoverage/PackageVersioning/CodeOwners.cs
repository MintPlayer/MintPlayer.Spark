using CodeCoverage.Git;

namespace CodeCoverage.PackageVersioning;

/// <summary>
/// A repository's CODEOWNERS file (release lines recovery plan, segment F): who owns which paths, for asking about a held
/// backport whose author cannot fix it (a fork's or a bot's pull request, PRD Q8).
/// </summary>
/// <remarks>
/// GitHub's rules, as far as owners go: the last matching line wins, a line without owners un-owns its paths, a pattern
/// without a slash (or with only a trailing one) matches at any depth, a leading slash or one inside the pattern anchors
/// it at the root, and a pattern that names a directory owns everything below it. Negation (<c>!</c>) and character
/// ranges (<c>[ ]</c>) are not CODEOWNERS syntax; a line using them is skipped, as GitHub skips a line it cannot read.
/// </remarks>
public sealed class CodeOwners
{
    /// <summary>Where GitHub looks for the file, in its order: the first one that exists is the one it uses.</summary>
    public static readonly IReadOnlyList<string> Locations = [".github/CODEOWNERS", "CODEOWNERS", "docs/CODEOWNERS"];

    private readonly List<(PathGlob[] Globs, string[] Owners)> rules;

    private CodeOwners(List<(PathGlob[] Globs, string[] Owners)> rules) => this.rules = rules;

    public static CodeOwners Empty { get; } = new([]);

    public static CodeOwners Parse(string? text)
    {
        var rules = new List<(PathGlob[], string[])>();
        foreach (var raw in (text ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            var hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash].Trim();
            if (line.Length == 0) continue;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (Globs(parts[0]) is not { } globs) continue;
            var owners = parts.Skip(1).Where(o => o.StartsWith('@') && o.Length > 1).ToArray();
            rules.Add((globs, owners));
        }
        return new(rules);
    }

    /// <summary>The owners of <paramref name="path"/> (<c>@user</c> or <c>@org/team</c>), empty when nobody owns it.</summary>
    public IReadOnlyList<string> OwnersOf(string path)
    {
        for (var i = rules.Count - 1; i >= 0; i--)
            if (rules[i].Globs.Any(g => g.Matches(path)))
                return rules[i].Owners;
        return [];
    }

    /// <summary>The owners of any of <paramref name="paths"/>, in first-seen order.</summary>
    public IReadOnlyList<string> OwnersOf(IEnumerable<string> paths)
        => [.. paths.SelectMany(OwnersOf).Distinct(StringComparer.OrdinalIgnoreCase)];

    private static PathGlob[]? Globs(string pattern)
    {
        if (pattern.StartsWith('!') || pattern.Contains('[') || pattern.Contains('\\')) return null;
        var directory = pattern.EndsWith('/');
        var body = pattern.Trim('/');
        if (body.Length == 0) return null;
        // A slash at the start or inside anchors the pattern at the root; otherwise it matches at any depth.
        var anchored = pattern.StartsWith('/') || body.Contains('/');
        var glob = anchored || body.StartsWith("**", StringComparison.Ordinal) ? body : $"**/{body}";
        // `docs/` owns what is below docs; `docs` (or `*.md`) owns the path itself and, when it is a directory, what is below it.
        return directory ? [new PathGlob($"{glob}/**")] : [new PathGlob(glob), new PathGlob($"{glob}/**")];
    }
}
