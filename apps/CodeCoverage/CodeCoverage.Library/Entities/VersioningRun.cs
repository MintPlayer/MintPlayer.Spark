using CodeCoverage.Forge;
using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.Entities;

/// <summary>
/// One server-host versioning run (docs/coverage_package_versioning_PRD.md D2, D5; plan M6): the manifest a publish workflow
/// sent, what the engine planned and which tags it wrote.
/// </summary>
/// <remarks>
/// <para>
/// One document per workflow run attempt, <c>VersioningRuns/{provider}/{repoId}/{runId}.{attempt}</c>, stored and loaded by id
/// (no query, so no RQL to splice into). A retried request of the same attempt overwrites its record: the engine is
/// idempotent, so the second answer is the one that holds.
/// </para>
/// <para>
/// A read-only Spark entity since M12 (D16): the repository page lists its runs through <c>Custom.Repository_VersioningRuns</c>,
/// which streams this repository's id prefix, so no index is built. The nested lists stay out of the model; the page shows
/// them as the <see cref="Tags"/> and <see cref="RefusedPackages"/> summaries.
/// </para>
/// </remarks>
public class VersioningRun
{
    public const string Prefix = "VersioningRuns/";
    public const int MaxNotes = 500;

    public static string DocumentId(EForgeProvider provider, long repositoryId, long runId, int runAttempt)
        => $"{ForRepository(provider, repositoryId)}{runId}.{runAttempt}";

    /// <summary>The id prefix every run of one repository shares.</summary>
    public static string ForRepository(EForgeProvider provider, long repositoryId)
        => $"{Prefix}{provider.ToString().ToLowerInvariant()}/{repositoryId}/";

    public string? Id { get; set; }

    /// <summary>The <see cref="Entities.Repository"/> document id.</summary>
    [Reference(typeof(Repository))]
    public string Repository { get; set; } = string.Empty;

    public string Branch { get; set; } = string.Empty;

    public string Commit { get; set; } = string.Empty;

    /// <summary>The token's <c>workflow_ref</c> (S6): which workflow asked for the tags.</summary>
    public string? WorkflowRef { get; set; }

    public long RunId { get; set; }

    public int RunAttempt { get; set; }

    public DateTime StartedAtUtc { get; set; }

    public DateTime? FinishedAtUtc { get; set; }

    /// <summary>The git writer's outcome (<c>versioned</c>, <c>plan-refused</c>, <c>failed</c>, …), or the app's own refusal.</summary>
    public string? Outcome { get; set; }

    public string? Reason { get; set; }

    /// <summary>The writer's diagnostic text; kept for the operators, not drawn on the page.</summary>
    [IgnoreProperty]
    public string? Detail { get; set; }

    [IgnoreProperty]
    public List<VersioningRunRelease> Releases { get; set; } = [];

    [IgnoreProperty]
    public List<VersioningRunNote> Refused { get; set; } = [];

    /// <summary>The tags this run placed or found, for the repository page.</summary>
    public string Tags => string.Join(", ", Releases.Select(r => r.Tag).Where(t => t.Length > 0));

    /// <summary>The packages the plan refused, for the repository page.</summary>
    public string RefusedPackages => string.Join(", ", Refused.Select(r => r.Id));

    public int Skipped { get; set; }

    [IgnoreProperty]
    public List<string> Warnings { get; set; } = [];

    /// <summary>The release branches a platform major move asks for (D12), created behind the auto-cut policy.</summary>
    [IgnoreProperty]
    public List<VersioningRunBranchCut> BranchCuts { get; set; } = [];
}

public sealed class VersioningRunBranchCut
{
    public string Branch { get; set; } = string.Empty;
    public int Major { get; set; }

    /// <summary>The release commit's first parent: the last commit of the old line.</summary>
    public string Commit { get; set; } = string.Empty;

    public List<string> Packages { get; set; } = [];
    public int? PullRequest { get; set; }

    /// <summary><c>created</c>, <c>exists</c>, <c>disabled</c> or <c>invalid</c> once handled; null while pending.</summary>
    public string? Outcome { get; set; }
}

public sealed class VersioningRunRelease
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
    public List<string> Reasons { get; set; } = [];
    public bool TaggedByHand { get; set; }

    /// <summary><c>created</c>, <c>already-present</c>, <c>conflict</c> or <c>unreachable</c>; null for a tag a human placed.</summary>
    public string? TagOutcome { get; set; }

    /// <summary>The GitHub Release to create for the tag (D15); null when the tag is not in place.</summary>
    public VersioningRunReleaseNotes? Release { get; set; }

    /// <summary><c>created</c> or <c>exists</c> once the Release step handled it; null while pending or with Releases off.</summary>
    public string? ReleaseOutcome { get; set; }
}

public sealed class VersioningRunReleaseNotes
{
    public string Name { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public bool Prerelease { get; set; }
}

public sealed class VersioningRunNote
{
    public string Id { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}
