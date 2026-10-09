using System.Text.RegularExpressions;

namespace CodeCoverage.Dependencies;

/// <summary>
/// GitHub Actions workflows (<c>.github/workflows/*.yml</c>) and composite actions
/// (<c>action.yml</c>): every <c>uses: owner/repo[/path]@ref</c> consumes the action
/// <c>owner/repo</c>, lowercased.
/// </summary>
/// <remarks>
/// <para>
/// Read line by line rather than as YAML. <c>uses:</c> is a scalar on one line in every form GitHub
/// accepts, and a line scan keeps working on a file with a YAML error elsewhere — which a workflow
/// that GitHub itself refuses to run can still have, and it is still a dependency.
/// </para>
/// <para>
/// Local actions (<c>./path</c>) and container actions (<c>docker://…</c>) are skipped: the first is
/// the repository itself, the second is an image, not an action. A reusable workflow
/// (<c>owner/repo/.github/workflows/x.yml@ref</c>) consumes its repository like any action.
/// </para>
/// <para>
/// Every repository implicitly produces the action named after it; the dependency graph adds that
/// itself from the repository's current name, so a rename never leaves a stale product behind.
/// </para>
/// </remarks>
public static partial class ActionsManifestParser
{
    public static ManifestParseResult Parse(string path, string content)
    {
        var collector = new ManifestCollector(path);

        foreach (var raw in content.Split('\n'))
        {
            var match = UsesPattern().Match(raw);
            if (!match.Success) continue;

            var value = match.Groups["value"].Value.Trim();
            if (value.StartsWith("./", StringComparison.Ordinal) || value.StartsWith("docker://", StringComparison.OrdinalIgnoreCase))
                continue;

            var at = value.LastIndexOf('@');
            if (at <= 0) continue;
            var reference = value[(at + 1)..];
            var segments = value[..at].Split('/');
            if (segments.Length < 2 || segments[0].Length == 0 || segments[1].Length == 0) continue;
            if (segments[0].Contains("${{", StringComparison.Ordinal) || segments[1].Contains("${{", StringComparison.Ordinal)) continue;

            collector.Consume(ManifestEcosystems.Actions, $"{segments[0]}/{segments[1]}".ToLowerInvariant(), reference, dev: false);
        }

        return collector.Result();
    }

    /// <summary>The implicit product of a repository: the action named after it.</summary>
    public static string ProducedName(string fullName) => fullName.ToLowerInvariant();

    // `uses:` as a mapping key, optionally as the first key of a list item, with an optional quote.
    [GeneratedRegex(@"^\s*(?:-\s+)?uses\s*:\s*['""]?(?<value>[^'""\s#]+)")]
    private static partial Regex UsesPattern();
}
