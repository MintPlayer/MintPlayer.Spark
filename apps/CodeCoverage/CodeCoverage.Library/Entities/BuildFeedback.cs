namespace CodeCoverage.Entities;

/// <summary>
/// Outbox state for the check-runs a build owes GitHub. Lives on the Build so
/// the publish is exactly-once-ish per finalize: the recipient records what it
/// posted (check-run ids key the updates on re-finalize) and the sweep cron
/// re-broadcasts what still needs attempting. The two queryable mirrors on
/// Build (<see cref="Build.FeedbackState"/>, <see cref="Build.FeedbackNextAttemptAtUtc"/>)
/// exist because this object is deliberately not indexed.
/// </summary>
public class BuildFeedback
{
    /// <summary>Pending | Posted | Retry | Unavailable | Failed.</summary>
    public string State { get; set; } = "Pending";

    public int Attempts { get; set; }

    public DateTime? NextAttemptAtUtc { get; set; }

    public long? ProjectCheckRunId { get; set; }

    public long? PatchCheckRunId { get; set; }

    public string? Error { get; set; }
}
