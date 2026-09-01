using MintPlayer.Spark.Messaging.Abstractions;

namespace CodeCoverage.Ingestion;

/// <summary>
/// Queued when an upload lands; processed by <see cref="ParseSessionRecipient"/>.
/// Explicit queue name — never rely on the FullName fallback.
/// </summary>
[MessageQueue("coverage-parse-session")]
public record ParseSessionMessage
{
    public required string BuildId { get; init; }
    public required string SessionId { get; init; }
}

/// <summary>
/// Explicit finish. Deliberately on the SAME queue as parsing: the queue is
/// strict FIFO, so by the time this message runs, every parse session enqueued
/// before the finish call has completed — finalization always sees fresh state
/// and never races a concurrent parse's save.
/// </summary>
[MessageQueue("coverage-parse-session")]
public record FinalizeBuildMessage
{
    public required string BuildId { get; init; }
}
