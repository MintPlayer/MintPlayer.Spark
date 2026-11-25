using CodeCoverage.Forge;
using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.Entities;

/// <summary>
/// #693 Q12: what the next release of one line would list (docs/coverage_changelog_PRD.md, Q12 "A"). The entries since each
/// package's last tag, composed by the git writer's plan job after every push to the line; with promote lines, a current
/// line is composed on every default-branch push as if promoted (docs/coverage_changelog_release_lines_PRD.md R1).
/// </summary>
/// <remarks>
/// <para>
/// One document per (repository, branch), <c>UpcomingChangelogs/{provider}/{repoId}/{escaped branch}</c>, replaced on each
/// push through <c>ISparkSession</c> (D16). The branch is escaped so <c>release</c> and <c>release/x</c> never share a prefix.
/// </para>
/// <para>
/// The notes come from the commits alone: the writer's token reads contents only, so a pull request's <c>## Changelog</c>
/// section and <c>changelog: skip</c> label (D9) apply at release time, not here. The page says so.
/// </para>
/// <para>A read-only Spark entity: the repository page lists it through <c>Custom.Repository_UpcomingChangelog</c>.</para>
/// </remarks>
[IdKind(SparkIdKind.Natural)]
public partial class UpcomingChangelog
{
    public const string Prefix = "UpcomingChangelogs/";

    public static string DocumentId(EForgeProvider provider, long repositoryId, string branch)
        => $"{ForRepository(provider, repositoryId)}{Uri.EscapeDataString(branch)}";

    /// <summary>The id prefix every line of one repository shares.</summary>
    public static string ForRepository(EForgeProvider provider, long repositoryId)
        => $"{Prefix}{provider.ToString().ToLowerInvariant()}/{repositoryId}/";

    public Id<UpcomingChangelog> Id { get; set; }

    /// <summary>The repository the line belongs to.</summary>
    [Reference(typeof(Repository))]
    public string Repository { get; set; } = string.Empty;

    /// <summary>The release line, or the default branch.</summary>
    public string Branch { get; set; } = string.Empty;

    /// <summary>The commit the notes were composed at.</summary>
    public string Commit { get; set; } = string.Empty;

    /// <summary>When the notes were composed.</summary>
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>The packages a release would move.</summary>
    public string Packages => string.Join(", ", Releases.Select(r => r.Package));

    /// <summary>What the next release would list per package, before the pull requests' Changelog sections and labels.</summary>
    public string Notes => string.Join("\n\n", Releases.Select(r => $"## {r.Package}\n\n{(r.Omitted ? "(too long to show)" : r.Notes)}"));

    [IgnoreProperty]
    public List<UpcomingChangelogRelease> Releases { get; set; } = [];

    /// <summary>
    /// On the default branch's document only: <c>versioning.lines.pattern</c> when lines are on, which the push filter
    /// matches a release line's pushes against without reading <c>coverage.yml</c>.
    /// </summary>
    [IgnoreProperty]
    public string? LinePattern { get; set; }

    /// <summary>On the default branch's document only: <c>versioning.releaseBranches</c>, defaults applied.</summary>
    [IgnoreProperty]
    public List<string> ReleaseBranches { get; set; } = [];
}

public sealed class UpcomingChangelogRelease
{
    public string Package { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public bool Omitted { get; set; }
}
