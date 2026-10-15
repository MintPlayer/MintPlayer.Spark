using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using MintPlayer.Spark.Messaging.Abstractions;

namespace CodeCoverage.MergeQueue;

/// <summary>
/// One step of one entry's run (merge-queue PRD §5.1, §5.5), scheduled by the saga itself through the transactional
/// outbox, so "the state changed" and "the next step is queued" commit together or not at all.
/// </summary>
/// <remarks>
/// Gated on <see cref="Run"/> and <see cref="Generation"/>: a step for a state the entry has left is a no-op, which is
/// what makes a late poll, a timeout that lost its race, or a redelivered step harmless.
/// </remarks>
[MessageQueue(CoverageQueues.MergeQueue)]
public sealed record MergeQueueStepMessage
{
    public required string EntryId { get; init; }
    public required int Run { get; init; }
    public required int Generation { get; init; }
    public required EMergeQueueStep Step { get; init; }

    /// <summary>For <see cref="EMergeQueueStep.Cleanup"/>: the queue branch to delete and the commit it must still be at.</summary>
    public string? QueueBranch { get; init; }

    public string? ExpectedSha { get; init; }

    /// <summary>For <see cref="EMergeQueueStep.CancelRun"/>: the abandoned regenerate run.</summary>
    public long? RunId { get; init; }
}

public enum EMergeQueueStep
{
    /// <summary>Read the pull request and the base; merge directly (O20 case 1) or prepare on the writer.</summary>
    Start,

    /// <summary>Delete a queue branch that is out of step with the entry, then start again.</summary>
    ResetQueueBranch,

    /// <summary>Check the workflow file (D12) and dispatch the regenerate run (D14).</summary>
    Dispatch,

    /// <summary>Fallback read of the regenerate run, for a lost <c>workflow_run</c> webhook.</summary>
    PollRun,

    /// <summary>Download the patch and write the final commit to the pull request's branch.</summary>
    Publish,

    /// <summary>Fallback read of the checks, for lost check webhooks.</summary>
    PollChecks,

    /// <summary>Re-check the actor and the base, then ask the forge to merge (D13).</summary>
    Merge,

    /// <summary>Read the pending merge request (D17).</summary>
    PollMerge,

    /// <summary>The current wait's deadline (regenerate or checks).</summary>
    Timeout,

    /// <summary>Delete the queue branch of a finished run (D5). Not gated: the entry may already run again.</summary>
    Cleanup,

    /// <summary>
    /// Cancel a regenerate run the entry stopped waiting for (§0 D53). Not gated either: the entry has moved on, which is
    /// why the run is cancelled.
    /// </summary>
    CancelRun,

    /// <summary>
    /// Close the issues a merged pull request links, when the forge has not (§0 D106). Not gated on the run either: the
    /// entry has ended by design, and a redelivered step skips the issues that are closed already.
    /// </summary>
    CloseIssues,
}

/// <summary>
/// Something happened that the queue may care about: a command, a push, a finished run, a check. Forwarded from the
/// neutral forge events onto the saga's lane, so it is serialised with the steps.
/// </summary>
[MessageQueue(CoverageQueues.MergeQueue)]
public sealed record MergeQueueSignalMessage
{
    public required string RepositoryId { get; init; }
    public required EMergeQueueSignal Kind { get; init; }
    public int? Number { get; init; }
    public string? Sha { get; init; }
    public string? Branch { get; init; }
    public long? RunId { get; init; }
    public EMergeQueueCommand? Command { get; init; }
    public string? Actor { get; init; }
    public EMergeQueueCommandSource? Source { get; init; }
    public string? Label { get; init; }
}

public enum EMergeQueueSignal
{
    /// <summary>
    /// Enqueue or dequeue (<see cref="MergeQueueSignalMessage.Command"/>), from a label, a comment, the panel's button or
    /// the check's button.
    /// </summary>
    Command,

    /// <summary>The pull request's head moved to <see cref="MergeQueueSignalMessage.Sha"/>.</summary>
    HeadPushed,

    /// <summary>Branch <see cref="MergeQueueSignalMessage.Branch"/> moved to <see cref="MergeQueueSignalMessage.Sha"/> (a base moving).</summary>
    BranchPushed,

    /// <summary>CI run <see cref="MergeQueueSignalMessage.RunId"/> finished.</summary>
    RunCompleted,

    /// <summary>A check on <see cref="MergeQueueSignalMessage.Sha"/> changed.</summary>
    ChecksChanged,

    /// <summary>The pull request was closed without merging.</summary>
    Closed,

    /// <summary>
    /// The pull request was merged (by the queue's request, or by somebody else). <see cref="MergeQueueSignalMessage.Sha"/>
    /// is the merge commit when the forge's event named it (§0 D94), else null.
    /// </summary>
    Merged,

    /// <summary>A review was submitted or dismissed.</summary>
    Reviewed,

    /// <summary>
    /// A pull request the queue does not hold was opened, reopened, pushed to, marked ready or retargeted: the queue may
    /// offer it the "Add to merge queue" button on its own check (merge-queue PRD §0 O30).
    /// <see cref="MergeQueueSignalMessage.Sha"/> is the head the event named; the saga reads the pull request again.
    /// </summary>
    Offer,
}
