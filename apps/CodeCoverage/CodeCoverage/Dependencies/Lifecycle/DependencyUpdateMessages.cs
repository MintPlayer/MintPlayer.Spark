using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using MintPlayer.Spark.Messaging.Abstractions;

namespace CodeCoverage.Dependencies.Lifecycle;

/// <summary>
/// One step of one repository's dependency run (dependency-updates PRD §13.8 N5), queued by the saga itself through the
/// outbox, so "the state changed" and "the next step is queued" commit together. Gated on <see cref="Run"/> and
/// <see cref="Generation"/>, like the merge queue's steps: a step for a state the run has left is a no-op.
/// </summary>
[MessageQueue(CoverageQueues.DependencyUpdates)]
public sealed record DependencyUpdateStepMessage
{
    public required string RepositoryId { get; init; }
    public required int Run { get; init; }
    public required int Generation { get; init; }
    public required EDependencyStep Step { get; init; }

    /// <summary><see cref="EDependencyStep.CancelRun"/>: the abandoned workflow run.</summary>
    public long? RunId { get; init; }

    /// <summary><see cref="EDependencyStep.Cleanup"/>: the commit the work ref must still be at to be deleted.</summary>
    public string? ExpectedSha { get; init; }
}

public enum EDependencyStep
{
    /// <summary>Read <c>coverage.yml</c> and the default branch; dispatch the lookup.</summary>
    Start,

    /// <summary>Fallback read of the pending workflow run, for a lost <c>workflow_run</c> webhook.</summary>
    PollRun,

    /// <summary>The pending workflow run's deadline.</summary>
    Timeout,

    /// <summary>Policy, plan commit (R14), apply dispatch.</summary>
    Plan,

    /// <summary>Rebuild the rolling branch (R4) and upsert the pull request.</summary>
    Publish,

    /// <summary>Fallback read of the rolling head's checks.</summary>
    PollChecks,

    /// <summary>The wait for the rolling head's checks is over.</summary>
    ChecksTimeout,

    /// <summary>Open the onboarding pull request (R16).</summary>
    Onboard,

    /// <summary>Delete the work ref (R14). Not gated: the run may already have moved on.</summary>
    Cleanup,

    /// <summary>Cancel a workflow run the saga stopped waiting for (§0 D53). Not gated.</summary>
    CancelRun,
}

/// <summary>Something the saga may care about, forwarded onto its lane so it is serialised with the steps.</summary>
[MessageQueue(CoverageQueues.DependencyUpdates)]
public sealed record DependencyUpdateSignalMessage
{
    public required string RepositoryId { get; init; }
    public required EDependencySignal Kind { get; init; }

    public EDependencyRunTrigger? Trigger { get; init; }

    /// <summary>R10: the vulnerable dependencies of a security trigger.</summary>
    public IReadOnlyList<DependencySecurityFixRecord>? SecurityFixes { get; init; }

    public long? RunId { get; init; }
    public string? Sha { get; init; }
    public int? Number { get; init; }
    public long? CommentId { get; init; }
    public EDependencyCommand? Command { get; init; }
    public string? Argument { get; init; }
    public string? Actor { get; init; }

    /// <summary>
    /// A person is waiting on this signal (a repository's own switch, "Run the scheduled slot now"): it was broadcast
    /// expedited, and the run it starts schedules its first step expedited too (D-O15). A message cannot read its own
    /// <c>BroadcastOptions</c>, so the sender states it here as well.
    /// </summary>
    public bool Expedite { get; init; }
}

public enum EDependencySignal
{
    /// <summary>Start a run (<see cref="DependencyUpdateSignalMessage.Trigger"/>), or note it for after the current one.</summary>
    Trigger,

    /// <summary>A workflow run finished (<see cref="DependencyUpdateSignalMessage.RunId"/>).</summary>
    RunCompleted,

    /// <summary>A check on <see cref="DependencyUpdateSignalMessage.Sha"/> changed.</summary>
    ChecksChanged,

    /// <summary>A comment command on <see cref="DependencyUpdateSignalMessage.Number"/> (§7.5).</summary>
    Command,

    /// <summary>A pull request was merged (the rolling one, or the onboarding one).</summary>
    Merged,

    /// <summary>A pull request was closed without merging.</summary>
    Closed,

    /// <summary>The feature is enabled for the repository: open the onboarding pull request when it needs one (R16).</summary>
    Onboard,

    /// <summary>
    /// The feature was switched off (R24): close the open rolling pull request with a comment, delete the bot's branches,
    /// reset the pull request and branch state. Holds and approvals are kept; the templates and <c>coverage.yml</c> are the
    /// owner's code and stay.
    /// </summary>
    Offboard,
}

/// <summary>
/// §7.6's debounce: a producer released or pushed at <see cref="EventAtUtc"/>; 30 minutes later, if nothing newer came from it,
/// its consumers on the account get an own-release run.
/// </summary>
[MessageQueue(CoverageQueues.DependencyUpdates)]
public sealed record DependencyPropagationMessage
{
    public required string AccountId { get; init; }
    public required string ProducerRepositoryId { get; init; }
    public required DateTime EventAtUtc { get; init; }
}
