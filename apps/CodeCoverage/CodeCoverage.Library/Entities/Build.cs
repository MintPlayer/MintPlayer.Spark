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
    /// <summary>Document id of this build, <c>Commits/{repoGitHubId}/{sha}/builds/{runId}-{runAttempt}</c>.</summary>
    public string? Id { get; set; }

    /// <summary>The commit this CI run measured coverage for.</summary>
    [Reference(typeof(Commit))]
    public string? Commit { get; set; }

    /// <summary>"Open" while uploads may still arrive; "Finalized" once closed.</summary>
    public string Status { get; set; } = "Open";

    /// <summary>The GitHub Actions workflow run id all uploads of this build came from.</summary>
    public long CiRunId { get; set; }

    /// <summary>The attempt number of that workflow run; a re-run of the same run creates a separate build.</summary>
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

    /// <summary>Name of the GitHub Actions workflow that produced this build (e.g. <c>CI</c>).</summary>
    public string? WorkflowName { get; set; }

    /// <summary>The GitHub event that triggered the run, e.g. <c>push</c> or <c>pull_request</c>.</summary>
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

    /// <summary>
    /// Declared by the uploader: the test step that produced this run's reports
    /// succeeded, so files it did not measure may be filled in from the base.
    /// False when any job of the run said its tests failed — a crashed suite
    /// emits no report, and the server cannot tell "affected but crashed" from
    /// "unaffected", so the assembler carries nothing for the commit instead of
    /// papering over the crash with the base's numbers.
    /// </summary>
    public bool CarryForward { get; set; } = true;

    /// <summary>When the first upload of this run created the build (UTC).</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>When the most recent upload arrived (UTC); the debounce finalize counts from here.</summary>
    public DateTime? LastUploadAtUtc { get; set; }

    /// <summary>When the build was closed and its coverage computed (UTC); null while still open.</summary>
    public DateTime? FinalizedAtUtc { get; set; }

    /// <summary>"Explicit" | "Debounce" | "Timeout"</summary>
    public string? FinalizeReason { get; set; }

    /// <summary>The individual uploads (one per action invocation) merged into this build.</summary>
    public List<BuildSession> Sessions { get; set; } = [];

    /// <summary>Whole-build line and branch totals, merged across all sessions at finalize; null while the build is open.</summary>
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
