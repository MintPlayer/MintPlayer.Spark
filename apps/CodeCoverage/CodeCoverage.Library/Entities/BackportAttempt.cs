using CodeCoverage.Forge;
using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.Entities;

/// <summary>
/// What became of one backport of one pull request to one release line (docs/release_lines_recovery_plan.md, segment B):
/// a pull request, a draft, or a failure. Every backport the App was asked for ends here, so none is lost to a comment
/// nobody read or a log line.
/// </summary>
/// <remarks>
/// <para>
/// One document per backported pull request and line, <c>BackportAttempts/{provider}/{repoId}/{source}/{line}</c>, stored
/// by id (no query, so no RQL to splice into). A retry (the label added again, <c>/backport</c>) overwrites it: the latest
/// outcome is the one that holds.
/// </para>
/// <para>
/// A read-only Spark entity: the repository page lists them through <c>Custom.Repository_BackportAttempts</c>, which
/// streams this repository's id prefix, so no index is built.
/// </para>
/// </remarks>
public class BackportAttempt
{
    public const string Prefix = "BackportAttempts/";
    public const int MaxDetail = 2000;

    /// <summary>The pull request is open.</summary>
    public const string Opened = "opened";
    /// <summary>The pull request is open, with stale regenerated files a person has to fix.</summary>
    public const string NotRegenerated = "opened, not regenerated";
    /// <summary>A draft with the conflict markers committed.</summary>
    public const string Drafted = "draft, conflicts";
    /// <summary>The branch is pushed; the pull request opens once the line's workflow regenerated it.</summary>
    public const string Regenerating = "regenerating";
    /// <summary>The line holds the change already.</summary>
    public const string AlreadyOnLine = "already on the line";
    /// <summary>The backport branch exists already; nothing was pushed.</summary>
    public const string BranchExists = "branch exists";
    /// <summary>It conflicts and the repository asks for a comment, not a draft: a person backports it by hand.</summary>
    public const string Conflicted = "conflicts";
    /// <summary>It could not be done: the writer was unavailable or refused, the push was rejected, the forge failed.</summary>
    public const string Failed = "failed";
    /// <summary>The merge queue holds its pull request on the line (segment C), and the line waits for it (segment F).</summary>
    public const string Held = "held";

    public static string DocumentId(EForgeProvider provider, long repositoryId, int source, string branch)
        => $"{ForRepository(provider, repositoryId)}{source}/{branch.Replace('/', '-')}";

    /// <summary>The id prefix every attempt of one repository shares.</summary>
    public static string ForRepository(EForgeProvider provider, long repositoryId)
        => $"{Prefix}{provider.ToString().ToLowerInvariant()}/{repositoryId}/";

    public string? Id { get; set; }

    [Reference(typeof(Repository))]
    public string Repository { get; set; } = string.Empty;

    /// <summary>The backported pull request.</summary>
    public int Source { get; set; }

    /// <summary>Who opened the backported pull request.</summary>
    public string? SourceAuthor { get; set; }

    /// <summary>Its merge commit on the default branch.</summary>
    public string? SourceSha { get; set; }

    /// <summary>The release line.</summary>
    public string Branch { get; set; } = string.Empty;

    /// <summary>What became of it: opened, draft, regenerating, conflicts, failed, …</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>The backport pull request, when one was opened.</summary>
    public int? PullRequest { get; set; }

    /// <summary>Why it failed or conflicts.</summary>
    public string? Detail { get; set; }

    public DateTime AttemptedAtUtc { get; set; }
}
