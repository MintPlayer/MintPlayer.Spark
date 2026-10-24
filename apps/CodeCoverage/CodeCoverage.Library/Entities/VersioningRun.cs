using CodeCoverage.Forge;

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
/// <para>Not a Spark entity: nothing here is edited through the generic UI.</para>
/// </remarks>
public class VersioningRun
{
    public const string Prefix = "VersioningRuns/";
    public const int MaxNotes = 500;

    public static string DocumentId(EForgeProvider provider, long repositoryId, long runId, int runAttempt)
        => $"{Prefix}{provider.ToString().ToLowerInvariant()}/{repositoryId}/{runId}.{runAttempt}";

    public string? Id { get; set; }

    /// <summary>The <see cref="Entities.Repository"/> document id.</summary>
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

    public string? Detail { get; set; }

    public List<VersioningRunRelease> Releases { get; set; } = [];

    public List<VersioningRunNote> Refused { get; set; } = [];

    public int Skipped { get; set; }

    public List<string> Warnings { get; set; } = [];
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
}

public sealed class VersioningRunNote
{
    public string Id { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}
