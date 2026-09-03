using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.Entities;

/// <summary>
/// Outbox state for the one comment the App owns on a pull request.
/// <para>
/// Keyed on the PULL REQUEST, not the build or the head sha — that is the whole
/// point. <see cref="BuildFeedback"/> is per-build, so a comment id stored there
/// would produce a fresh comment on every push; a PR with forty pushes would
/// carry forty coverage comments. One document per PR, edited in place, means
/// one comment and (measured) one subscriber notification.
/// </para>
/// <para>
/// State vocabulary deliberately mirrors <see cref="BuildFeedback"/> so the
/// existing sweep in PublishFeedbackCronJob applies unchanged.
/// </para>
/// </summary>
public class PullRequestFeedback
{
    public string? Id { get; set; }

    /// <summary>The repository this pull request belongs to.</summary>
    [Reference(typeof(Repository))]
    public string? Repository { get; set; }

    /// <summary>Number of the pull request this comment lives on.</summary>
    public int PullRequestNumber { get; set; }

    /// <summary>
    /// GitHub id of the comment already posted, so every later publish is an
    /// edit. Null before the first publish, and again if a human deletes the
    /// comment — the publisher then re-adopts by marker or creates a new one.
    /// </summary>
    public long? CommentId { get; set; }

    /// <summary>Head sha the comment currently describes; moves as the PR is pushed to.</summary>
    public string? LastPublishedSha { get; set; }

    /// <summary>
    /// Hash of the body last written. Measured: an edit produces no new
    /// timeline event and no re-notification, so this is NOT needed for
    /// notification hygiene — it only saves a pointless API call when a
    /// re-finalize changes nothing.
    /// </summary>
    public string? LastPublishedBodyHash { get; set; }

    public DateTime? LastPublishedAtUtc { get; set; }

    /// <summary>Pending | Posted | Retry | Unavailable | Failed.</summary>
    public string State { get; set; } = "Pending";

    /// <summary>How many times publishing this comment has been attempted.</summary>
    public int Attempts { get; set; }

    /// <summary>When the sweep should next try (UTC); null when no retry is scheduled.</summary>
    public DateTime? NextAttemptAtUtc { get; set; }

    /// <summary>Message of the last failed attempt; null when the last one succeeded.</summary>
    public string? Error { get; set; }

    public static string DocumentId(long repoGitHubId, int pullRequestNumber)
        => $"PullRequestFeedbacks/{repoGitHubId}/{pullRequestNumber}";
}
