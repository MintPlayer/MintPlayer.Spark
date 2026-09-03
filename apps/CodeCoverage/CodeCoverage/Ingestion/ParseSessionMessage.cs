using MintPlayer.Spark.Messaging.Abstractions;

namespace CodeCoverage.Ingestion;

/// <summary>
/// Queued when an upload lands; processed by <see cref="ParseSessionRecipient"/>.
/// Explicit queue name — never rely on the FullName fallback.
/// </summary>
[MessageQueue(Feedback.CoverageQueues.Ingestion)]
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
[MessageQueue(Feedback.CoverageQueues.Ingestion)]
public record FinalizeBuildMessage
{
    public required string BuildId { get; init; }
}

/// <summary>
/// Rebuild the commit's assembly after one of its builds finalized. Same
/// strict-FIFO queue again: assemblies of one commit never run concurrently
/// (so no lock is needed) and every parse enqueued before the finalize has
/// already landed.
/// </summary>
[MessageQueue(Feedback.CoverageQueues.Ingestion)]
public record AssembleCommitMessage
{
    public required string CommitId { get; init; }

    /// <summary>The build whose finalize triggered this; feedback is published for it once the assembly exists.</summary>
    public string? BuildId { get; init; }
}
