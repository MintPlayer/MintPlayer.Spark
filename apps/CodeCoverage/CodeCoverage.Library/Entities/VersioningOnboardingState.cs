using CodeCoverage.Onboarding;

namespace CodeCoverage.Entities;

/// <summary>
/// A repository's onboarding pull request and what follows its merge: since #571 D3 the chooser's
/// <c>mintplayer/onboarding</c> (<see cref="Features"/> set), before it #153 D17's <c>mintplayer/versioning-onboarding</c>.
/// The seed tags are pushed once it merges, whichever opened it. The name is kept: it is the production collection.
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

    /// <summary>#571 D3: the features the chooser was asked for; the pull request is rebuilt with them on every new
    /// default-branch head while it is open. Empty for a #153 versioning-only onboarding.</summary>
    public List<EOnboardingFeature> Features { get; set; } = [];

    /// <summary>#571 D3: the repository settings switched on once the pull request merges.</summary>
    public List<EOnboardingFeature> OnMerge { get; set; } = [];

    /// <summary>#571 D2: the old line branches renamed once the pull request merges.</summary>
    public List<OnboardingRename> Renames { get; set; } = [];

    /// <summary>docs/onboarding_line_cuts_PRD.md D5: the release lines created from the merge commit, after the renames.</summary>
    public List<string> Cuts { get; set; } = [];

    /// <summary>D2: the offered older lines the owner chose; a rebuild plans them again.</summary>
    public List<string> OlderLines { get; set; } = [];

    /// <summary>#638 D9: the App-made tags the owner chose to move onto their earliest occurrence, by name, at the backfill.</summary>
    public List<string> MoveTags { get; set; } = [];

    /// <summary>#571 D3: the merge commit the on-merge steps ran for; null while they are pending.</summary>
    public string? MergeHandledAt { get; set; }

    /// <summary>docs/onboarding_merge_backfill_PRD.md D1: the merge backfills the history, seeds or not. Set when an onboarding
    /// pull request opens; false on a state written before, so a deploy starts no backfill nobody asked for.</summary>
    public bool Backfill { get; set; }

    /// <summary>D1: the merge commit the history backfill ran for; null while it is pending.</summary>
    public string? BackfilledAt { get; set; }

    /// <summary>Seeds remain to be pushed.</summary>
    public bool SeedsPending => SeedCommit is null && Seeds.Count > 0 && PullRequestNumber is not null;

    /// <summary>D1: the history backfill remains to run. The owner's Move answer asks for it too, so a state written before
    /// <see cref="Backfill"/> existed (Tools #56) is honoured.</summary>
    public bool BackfillPending => BackfilledAt is null && PullRequestNumber is not null && (Backfill || MoveTags.Count > 0);

    /// <summary><c>Repositories/github/1</c> becomes <c>VersioningOnboardingStates/github/1</c>.</summary>
    public static string ForRepository(string repositoryId)
    {
        const string prefix = "Repositories/";
        if (!repositoryId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"'{repositoryId}' is not a repository document id.", nameof(repositoryId));
        return "VersioningOnboardingStates/" + repositoryId[prefix.Length..];
    }
}

public class OnboardingRename
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
}

public class VersioningOnboardingSeed
{
    public string PackageId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
}
