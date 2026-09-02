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

    /// <summary>How many times publishing the check-runs to GitHub has been attempted for this build.</summary>
    public int Attempts { get; set; }

    /// <summary>When the sweep should next try to publish (UTC); null when no retry is scheduled.</summary>
    public DateTime? NextAttemptAtUtc { get; set; }

    /// <summary>GitHub id of the project-coverage check-run already posted, so a re-finalize updates it instead of creating a duplicate.</summary>
    public long? ProjectCheckRunId { get; set; }

    /// <summary>GitHub id of the patch-coverage check-run already posted, so a re-finalize updates it instead of creating a duplicate.</summary>
    public long? PatchCheckRunId { get; set; }

    /// <summary>Message of the last failed publish attempt; null when the last attempt succeeded.</summary>
    public string? Error { get; set; }
}
