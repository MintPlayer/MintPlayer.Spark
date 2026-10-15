using CodeCoverage.Entities;

namespace CodeCoverage.MergeQueue;

/// <summary>What the queue does with a prepared pull request (O20 cases 2 and 3).</summary>
public enum EMergeQueuePlan
{
    /// <summary>Nothing to regenerate: publish the prepared commit as it is.</summary>
    Publish,

    /// <summary>Dispatch the regenerate workflow on the queue branch, then publish with its patch.</summary>
    Dispatch,

    /// <summary>A same-repository change under <c>.github/**</c> that needs the dispatch, queued without O22's bypass role
    /// (D12, O21 A): <c>touches-workflows</c>.</summary>
    EvictTouchesWorkflows,

    /// <summary>A fork's change under <c>.github/**</c> that needs the dispatch (O25): <c>fork-touches-workflows</c>. No
    /// role bypasses it.</summary>
    EvictForkTouchesWorkflows,
}

/// <summary>
/// The one place the merge queue decides whether it runs the regenerate workflow, and with it what a change under
/// <c>.github/**</c> may go through (D12, O21 A, O22, O25, merge-queue PRD §0 D63, D67). Nothing else computes "will
/// dispatch", so a dispatch can never be decided without the guard.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it matters only for the dispatch (O21 A, O25).</b> A dispatch needs the prepared commit on
/// <c>mq/&lt;base&gt;/pr-&lt;n&gt;</c>, a branch of the base repository. That push starts the repository's <c>push</c>
/// workflows from the pushed commit's own workflow files, and the dispatched run executes the queue branch's workflow
/// files (D12, D14, D19), both with the base repository's secrets (T5). Without a dispatch, nothing of the pull request
/// reaches a branch of the base repository before the merge (§0 D65): the rebased commit goes to the pull request's own
/// branch, a fork's to the fork, and its CI runs there exactly as it would without the queue. So a workflow-only change
/// (a Dependabot Actions bump) goes through, a fork's included.
/// </para>
/// <para>
/// <b>The invariants</b>, which <c>MergeQueueWorkflowGuardTests</c> holds over every combination of the inputs:
/// <list type="number">
/// <item>A dispatch never pushes, for a fork, a tree whose <c>.github</c> differs from the base's (O25). No role changes
/// that: it is the fork's code that would run with the base repository's secrets. The git writer refuses that push on
/// its own as well (<c>headFromFork</c> on the queue-branch push, §0 D67), should the saga ever ask.</item>
/// <item>A same-repository dispatch carries a <c>.github</c> change only through O22's bypass role. Its author has write
/// access, and their own push already ran those workflows.</item>
/// <item>Without a dispatch, a <c>.github</c> change never stops the entry, a fork's included.</item>
/// </list>
/// "Touches <c>.github/**</c>" is the writer's report of the prepared tree against its base (§0 D65), so a rename on the
/// base that carries the pull request's edit into a workflow counts as well.
/// </para>
/// </remarks>
public static class MergeQueueWorkflowGuard
{
    /// <summary>What follows a prepare.</summary>
    /// <param name="resolvedPaths">How many paths the resolver took from the base side.</param>
    /// <param name="touchesWorkflows">The prepared tree's <c>.github</c> differs from the base's, as the writer reported it.</param>
    /// <param name="isFork">The pull request's head lives in a fork.</param>
    /// <param name="bypassesQueueRules">The enqueuer's role reaches the bypass role (O22).</param>
    public static EMergeQueuePlan AfterPrepare(MergeQueueConfigSnapshot config, int resolvedPaths, bool touchesWorkflows, bool isFork, bool bypassesQueueRules)
        => AfterPrepare(RegenerateWorkflows(config, null, Enumerable.Repeat("?", resolvedPaths).ToList()), touchesWorkflows, isFork, bypassesQueueRules);

    /// <summary>What follows a prepare, given the regenerate workflows it needs (<see cref="RegenerateWorkflows"/>).</summary>
    public static EMergeQueuePlan AfterPrepare(IReadOnlyList<string> workflows, bool touchesWorkflows, bool isFork, bool bypassesQueueRules)
    {
        // Invariant 3: nothing of the pull request reaches the base repository before the merge.
        if (workflows.Count == 0)
            return EMergeQueuePlan.Publish;
        if (!touchesWorkflows)
            return EMergeQueuePlan.Dispatch;
        // Invariant 1: whoever queued it.
        if (isFork)
            return EMergeQueuePlan.EvictForkTouchesWorkflows;
        // Invariant 2.
        return bypassesQueueRules ? EMergeQueuePlan.Dispatch : EMergeQueuePlan.EvictTouchesWorkflows;
    }

    /// <summary>
    /// The regenerate workflows a prepared entry dispatches, one after the other, in ordinal order (O36):
    /// <list type="bullet">
    /// <item><c>postRebaseWhen: never</c>: none.</item>
    /// <item><c>always</c>: every workflow the configuration names (<c>postRebaseActionName</c> and each rule's
    /// <c>action</c>).</item>
    /// <item><c>auto-resolved</c>: the workflow of each resolution that regenerates: its rule's <c>action</c>, else
    /// <c>postRebaseActionName</c>. A keep-both-only resolution regenerates nothing and dispatches nothing.</item>
    /// </list>
    /// <paramref name="resolutions"/> null (a writer older than §10.6) counts every one of <paramref name="resolvedPaths"/>
    /// as regenerating with <c>postRebaseActionName</c>, as before.
    /// </summary>
    public static IReadOnlyList<string> RegenerateWorkflows(MergeQueueConfigSnapshot config, IReadOnlyList<MergeQueueResolution>? resolutions, IReadOnlyList<string> resolvedPaths)
    {
        IEnumerable<string?> workflows = config.PostRebaseWhen switch
        {
            EPostRebaseWhen.Never => [],
            EPostRebaseWhen.Always => config.AutoResolve.Select(r => r.Action).Append(config.PostRebaseActionName),
            _ when resolutions is null => resolvedPaths.Count > 0 ? [config.PostRebaseActionName] : [],
            _ => resolutions.Where(r => r.Regenerate)
                .Select(r => r.Rule >= 0 && r.Rule < config.AutoResolve.Count ? config.AutoResolve[r.Rule].Action ?? config.PostRebaseActionName : config.PostRebaseActionName),
        };
        return [.. workflows.OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>A path GitHub reads workflows from: the files its <c>workflows</c> permission guards (§0 D59). Compared
    /// case-insensitively, like D12's guard: a case-folding file system would read <c>.GitHub</c> as <c>.github</c>.</summary>
    public static bool IsWorkflowFile(string? path) => path is not null && path.StartsWith(".github/workflows/", StringComparison.OrdinalIgnoreCase);
}
