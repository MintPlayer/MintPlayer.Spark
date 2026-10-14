using CodeCoverage.Entities;

namespace CodeCoverage.MergeQueue;

/// <summary>What the queue does with a prepared pull request (O20 cases 2 and 3).</summary>
public enum EMergeQueuePlan
{
    /// <summary>Nothing to regenerate: publish the prepared commit as it is.</summary>
    Publish,

    /// <summary>Dispatch the regenerate workflow on the queue branch, then publish with its patch.</summary>
    Dispatch,

    /// <summary>The change touches <c>.github/**</c> and may not go where the plan would take it (D12, O21 A).</summary>
    EvictTouchesWorkflows,
}

/// <summary>
/// The one place the merge queue decides whether it runs the regenerate workflow, and with it what a change under
/// <c>.github/**</c> may go through (D12, O21 A, O22, merge-queue PRD §0 D63). Nothing else computes "will dispatch", so a
/// dispatch can never be decided without the guard.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it matters only for the dispatch (O21 A).</b> A dispatched run executes the workflow files of the queue branch
/// (D12), so a change there would run with the base repository's context in a run the queue started (T5). Without a
/// dispatch, the pull request's own CI has already run the same workflows, exactly as it would without the queue, so a
/// workflow-only change (a Dependabot Actions bump) goes through.
/// </para>
/// <para>
/// <b>A fork's change never goes anywhere (§0 D63).</b> The queue's push to <c>mq/…</c> before a dispatch is a push to a
/// branch of the base repository, and it alone starts the base's <c>push</c> workflows from the pushed commit's own
/// workflow files, with the base's secrets. So the writer refuses to prepare a fork's commit that changes <c>.github/**</c>
/// against its base (<see cref="MayPushWorkflowChanges"/>; §0 D65 reads that from the merged tree), and
/// <see cref="AfterPrepare"/> evicts one should the writer ever report it. A same-repository branch was pushed by someone
/// with write access, and its own push already ran those workflows. Since §0 D65 the queue branch is pushed only for a
/// dispatch, and for a fork only after <see cref="MergeQueueForkGuard"/> found no workflow that could run the fork's code
/// there with the base's secrets.
/// </para>
/// <para>
/// <b>O22's bypass role</b> lets a same-repository change that needs the dispatch through as well. Never a fork's: T5's
/// risk exists only for code that does not already have write access.
/// </para>
/// </remarks>
public static class MergeQueueWorkflowGuard
{
    /// <summary>
    /// Whether the writer may push this pull request's change to the base repository's queue branch when it touches
    /// <c>.github/**</c> (the prepare request's <c>allowWorkflowChanges</c>).
    /// </summary>
    public static bool MayPushWorkflowChanges(bool isFork) => !isFork;

    /// <summary>What follows a prepare.</summary>
    /// <param name="resolvedPaths">How many paths the resolver took from the base side.</param>
    /// <param name="touchesWorkflows">The net change touches <c>.github/**</c>, as the writer reported it.</param>
    /// <param name="bypassesQueueRules">The enqueuer's role reaches the bypass role (O22).</param>
    public static EMergeQueuePlan AfterPrepare(MergeQueueConfigSnapshot config, int resolvedPaths, bool touchesWorkflows, bool isFork, bool bypassesQueueRules)
    {
        var dispatch = config.PostRebaseActionName is not null && config.PostRebaseWhen switch
        {
            EPostRebaseWhen.Always => true,
            EPostRebaseWhen.Never => false,
            _ => resolvedPaths > 0,
        };
        if (!touchesWorkflows)
            return dispatch ? EMergeQueuePlan.Dispatch : EMergeQueuePlan.Publish;
        if (isFork)
            return EMergeQueuePlan.EvictTouchesWorkflows;
        if (!dispatch)
            return EMergeQueuePlan.Publish;
        return bypassesQueueRules ? EMergeQueuePlan.Dispatch : EMergeQueuePlan.EvictTouchesWorkflows;
    }

    /// <summary>A path GitHub reads workflows from: the files its <c>workflows</c> permission guards (§0 D59). Compared
    /// case-insensitively, like D12's guard: a case-folding file system would read <c>.GitHub</c> as <c>.github</c>.</summary>
    public static bool IsWorkflowFile(string? path) => path is not null && path.StartsWith(".github/workflows/", StringComparison.OrdinalIgnoreCase);
}
