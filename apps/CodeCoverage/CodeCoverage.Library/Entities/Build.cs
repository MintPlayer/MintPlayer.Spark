using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.Entities;

/// <summary>
/// One CI run's coverage bundle for a commit. All uploads from the same workflow
/// run (runId + runAttempt) land on the same Build as sessions and are merged.
/// Document id is Commits/{repoGitHubId}/{sha}/builds/{runId}-{runAttempt}.
/// </summary>
[GenerateIndex]
public class Build
{
    public string? Id { get; set; }

    [Reference(typeof(Commit))]
    public string? Commit { get; set; }

    /// <summary>"Open" while uploads may still arrive; "Finalized" once closed.</summary>
    public string Status { get; set; } = "Open";

    public long CiRunId { get; set; }

    public int CiRunAttempt { get; set; }

    /// <summary>
    /// "runId.attempt" display value for the generic grids (master-parity "Run"
    /// column). Deterministic from <see cref="CiRunId"/> and <see cref="CiRunAttempt"/>,
    /// both of which are fixed at creation.
    ///
    /// Stored rather than computed, which it was until the Builds index became
    /// generated: a generated index maps <c>build.Run</c> straight through, and that
    /// runs server-side against the JSON document — where a get-only CLR property
    /// does not exist. It would have indexed as null on every build, and the grid
    /// projects through the index, so the column would have quietly gone blank.
    /// Assign it through <see cref="ComposeRun"/> so the format lives in one place.
    /// </summary>
    public string Run { get; set; } = string.Empty;

    /// <summary>The one definition of the "Run" format; also used by the backfill migration.</summary>
    public static string ComposeRun(long ciRunId, int ciRunAttempt) => $"{ciRunId}.{ciRunAttempt}";

    public string? WorkflowName { get; set; }

    public string? EventName { get; set; }

    /// <summary>
    /// Declared by the uploader: this run measured only a subset of the
    /// workspace (e.g. <c>nx affected</c> on a pull request). Comparisons must
    /// scope or project rather than read the totals as whole-workspace, and a
    /// partial build never becomes a repository's headline number.
    /// </summary>
    public bool Partial { get; set; }

    /// <summary>
    /// The base sha the uploader's affected-computation actually ran against
    /// (what was passed to <c>nx affected --base</c>). Declared, not inferred —
    /// deliberately a dedicated field rather than <see cref="Entities.Commit.ParentSha"/>,
    /// which has two writers and a history of meaning drift and stays a hint.
    /// </summary>
    public string? DeclaredBaseSha { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? LastUploadAtUtc { get; set; }

    public DateTime? FinalizedAtUtc { get; set; }

    /// <summary>"Explicit" | "Debounce" | "Timeout"</summary>
    public string? FinalizeReason { get; set; }

    public List<BuildSession> Sessions { get; set; } = [];

    public CoverageSummary? Coverage { get; set; }

    /// <summary>Added-lines coverage vs the diff base; null when no diff was obtainable.</summary>
    public PatchCoverage? Patch { get; set; }

    /// <summary>
    /// Per-flag totals, keyed by sanitized flag name, computed at finalize
    /// from the per-flag merged file documents. Null for builds parsed before
    /// flags gained storage — attribution cannot be recovered from the merged
    /// build-level documents.
    /// </summary>
    public Dictionary<string, CoverageSummary>? FlagCoverage { get; set; }

    /// <summary>Check-run outbox; null until the first publish attempt is enqueued.</summary>
    public BuildFeedback? Feedback { get; set; }

    /// <summary>Queryable mirror of <see cref="BuildFeedback.State"/> — the sweep cron's filter.</summary>
    public string? FeedbackState { get; set; }

    /// <summary>Queryable mirror of <see cref="BuildFeedback.NextAttemptAtUtc"/>.</summary>
    public DateTime? FeedbackNextAttemptAtUtc { get; set; }

    /// <summary>
    /// The effective gate this build was judged by (settings document merged
    /// with the base ref's coverage.yml). Snapshotted because a base-dependent
    /// verdict is unexplainable later without its inputs.
    /// </summary>
    public GateSettings? GateSnapshot { get; set; }

    public static string DocumentId(long repoGitHubId, string sha, long runId, int runAttempt)
        => $"{Entities.Commit.DocumentId(repoGitHubId, sha)}/builds/{runId}-{runAttempt}";

    /// <summary>
    /// The one classification an API consumer is invited to branch on: is this
    /// build still working, did it finish cleanly, or did it finish with a
    /// number that under-counts? Derived here rather than by each caller so the
    /// status endpoint, the UI and any future check-run publisher can never
    /// disagree about what a build "is".
    /// <para>
    /// The internal vocabulary behind it is deliberately not the contract,
    /// because it is not frozen: T1.2 adds a partial-parse status for a session
    /// where only some reports were readable. Hence the shape of the two tests
    /// below — only "Pending" counts as in-flight, and cleanliness is
    /// "everything is exactly Parsed" rather than "nothing is Failed", so a new
    /// terminal status is absorbed into <c>CompleteWithErrors</c> without any
    /// consumer changing.
    /// </para>
    /// </summary>
    public static string ClassifyState(Build build)
    {
        if (build.Status != "Finalized" || build.Sessions.Any(s => s.ParseStatus == "Pending"))
            return "InFlight";

        // FinalizeReason "Timeout" already implies a Failed session — the cron
        // marks stragglers before closing — so this is belt-and-braces against a
        // future finalize path that times out without doing so.
        var clean = build.Sessions.All(s => s.ParseStatus == "Parsed") && build.FinalizeReason != "Timeout";
        return clean ? "Complete" : "CompleteWithErrors";
    }
}
