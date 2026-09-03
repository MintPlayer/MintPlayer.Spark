using MintPlayer.Spark.Messaging.Abstractions;

namespace CodeCoverage.Feedback;

/// <summary>
/// Asks for the sticky coverage comment of one pull request to be (re)published.
/// <para>
/// Two producers: the sweep cron, for a comment whose last attempt failed
/// transiently, and the pull_request webhook, for the pending comment when a PR
/// opens. The finalize path does NOT go through here — it publishes inline in
/// <see cref="PublishFeedbackRecipient"/>, where the check-run verdicts already
/// exist, so the comment and the checks are rendered from one set of numbers.
/// </para>
/// <para>
/// On the shared <see cref="CoverageQueues.Publishing"/> queue — off the
/// ingestion queue so a slow GitHub call cannot sit in front of report parsing,
/// but not on one of its own: see <see cref="CoverageQueues"/> for the cap.
/// </para>
/// </summary>
[MessageQueue(CoverageQueues.Publishing)]
public record PublishPullRequestCommentMessage
{
    /// <summary>Document id of the <c>PullRequestFeedback</c> to publish.</summary>
    public required string FeedbackId { get; init; }
}
