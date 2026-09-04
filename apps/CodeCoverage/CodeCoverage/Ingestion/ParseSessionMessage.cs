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
/// Explicit finish. Deliberately on the same lane as parsing AND partitioned by
/// the same BuildId: within a partition messages run oldest-first, so by the time
/// this runs, every parse of THIS build has completed. Parses of other builds run
/// in parallel and never delay it.
///
/// Queue-wide FIFO used to be the mechanism, and it was both too strong and too
/// weak — it made unrelated builds wait, and it did not survive a retry, because a
/// failed parse was written back behind everything broadcast since.
/// </summary>
[MessageQueue("coverage-parse-session")]
public record FinalizeBuildMessage
{
    public required string BuildId { get; init; }
}

/// <summary>
/// Rebuild the commit's assembly after one of its builds finalized. Partitioned by
/// CommitId rather than BuildId, because the requirement here is mutual exclusion
/// per commit (so no lock is needed) rather than ordering against parses — it is
/// only ever broadcast after the finalize it follows has completed.
///
/// Note this can now run alongside a parse of a SIBLING build of the same commit,
/// which queue-wide FIFO made impossible. That is safe only because
/// CommitAssembler.LoadContributingBuilds filters on Status == "Finalized", and a
/// build mid-parse is not finalized. Deleting that filter would reintroduce a race.
/// </summary>
[MessageQueue("coverage-parse-session")]
public record AssembleCommitMessage
{
    public required string CommitId { get; init; }

    /// <summary>The build whose finalize triggered this; feedback is published for it once the assembly exists.</summary>
    public string? BuildId { get; init; }
}
