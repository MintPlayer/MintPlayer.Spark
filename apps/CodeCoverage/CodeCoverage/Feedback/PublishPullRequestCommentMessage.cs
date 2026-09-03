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
/// Its own queue, for the same reason the check-run publish has one: a slow
/// GitHub call must never sit in front of report parsing, which is strict FIFO.
/// </para>
/// </summary>
[MessageQueue("coverage-publish-pr-comment")]
public record PublishPullRequestCommentMessage
{
    /// <summary>Document id of the <c>PullRequestFeedback</c> to publish.</summary>
    public required string FeedbackId { get; init; }
}
