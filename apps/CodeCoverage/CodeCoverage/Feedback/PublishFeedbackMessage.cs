using MintPlayer.Spark.Messaging.Abstractions;

namespace CodeCoverage.Feedback;

/// <summary>
/// Queued when a build finalizes (and by the sweep cron for retries);
/// processed by <see cref="PublishFeedbackRecipient"/>.
/// <para>
/// On <see cref="CoverageQueues.Publishing"/>, kept off the ingestion queue so a
/// slow GitHub API call cannot delay parsing, which is strict FIFO. It no longer
/// has that queue to itself — see <see cref="CoverageQueues"/> for the licence
/// cap that forces the sharing.
/// </para>
/// </summary>
[MessageQueue(CoverageQueues.Publishing)]
public record PublishFeedbackMessage
{
    public required string BuildId { get; init; }
}
