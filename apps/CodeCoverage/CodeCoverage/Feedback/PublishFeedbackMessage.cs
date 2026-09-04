using MintPlayer.Spark.Messaging.Abstractions;

namespace CodeCoverage.Feedback;

/// <summary>
/// Queued when a build finalizes (and by the sweep cron for retries);
/// processed by <see cref="PublishFeedbackRecipient"/>. Its own lane, declared
/// Concurrent: a slow GitHub call must never delay parsing, and one build's
/// feedback has nothing to do with another's.
/// </summary>
[MessageQueue("coverage-publish-feedback")]
public record PublishFeedbackMessage
{
    public required string BuildId { get; init; }
}
