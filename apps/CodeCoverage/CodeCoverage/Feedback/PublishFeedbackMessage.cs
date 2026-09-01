using MintPlayer.Spark.Messaging.Abstractions;

namespace CodeCoverage.Feedback;

/// <summary>
/// Queued when a build finalizes (and by the sweep cron for retries);
/// processed by <see cref="PublishFeedbackRecipient"/>. Its own queue — a slow
/// GitHub API call must never delay parsing, which shares strict FIFO.
/// </summary>
[MessageQueue("coverage-publish-feedback")]
public record PublishFeedbackMessage
{
    public required string BuildId { get; init; }
}
