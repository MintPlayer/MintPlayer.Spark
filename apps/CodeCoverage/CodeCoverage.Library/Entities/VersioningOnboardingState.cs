namespace CodeCoverage.Entities;

/// <summary>
/// A repository's versioning onboarding (#153 M13, PRD D17): the pull request the app opened on
/// <c>mintplayer/versioning-onboarding</c>, and the seed tags to push once it merges.
/// </summary>
/// <remarks>
/// The seeds are pushed at the pull request's merge commit, by whichever comes first: the merged webhook, or the first
/// versioning run (which may race it). Both read the merge commit from the forge, so neither trusts the other.
/// Loaded by id only (<see cref="ForRepository"/>); not indexed.
/// </remarks>
public class VersioningOnboardingState
{
    public string? Id { get; set; }

    /// <summary>The default branch's head the onboarding commit was made on; a retry rebuilds the same commit.</summary>
    public string? BaseSha { get; set; }

    public DateTime? StartedAtUtc { get; set; }

    /// <summary>The commit pushed to the onboarding branch.</summary>
    public string? BranchSha { get; set; }

    public int? PullRequestNumber { get; set; }

    /// <summary>The tags to push at the merge commit: the version each untagged package declared.</summary>
    public List<VersioningOnboardingSeed> Seeds { get; set; } = [];

    /// <summary>The merge commit the seeds were pushed at; null while they are pending.</summary>
    public string? SeedCommit { get; set; }

    /// <summary>What happened last, for the repository's owner: a refusal, a push, a failure.</summary>
    public string? Note { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>Seeds remain to be pushed.</summary>
    public bool SeedsPending => SeedCommit is null && Seeds.Count > 0 && PullRequestNumber is not null;

    /// <summary><c>Repositories/github/1</c> becomes <c>VersioningOnboardingStates/github/1</c>.</summary>
    public static string ForRepository(string repositoryId)
    {
        const string prefix = "Repositories/";
        if (!repositoryId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"'{repositoryId}' is not a repository document id.", nameof(repositoryId));
        return "VersioningOnboardingStates/" + repositoryId[prefix.Length..];
    }
}

public class VersioningOnboardingSeed
{
    public string PackageId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
}
