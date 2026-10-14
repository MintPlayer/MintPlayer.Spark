using CodeCoverage.Entities;
using CodeCoverage.MergeQueue;
using Xunit;

namespace CodeCoverage.Tests.MergeQueue;

/// <summary>
/// <see cref="MergeQueueWorkflowGuard"/>, the one place that decides whether the regenerate workflow is dispatched (merge-queue
/// PRD §0 D63, D67, D12, O21 A, O22, O25), over every combination of its inputs: 3 settings of <c>postRebaseWhen</c> × an
/// action or none × resolved or not × a <c>.github/**</c> change or none × a fork or not × the bypass role or not.
/// </summary>
public class MergeQueueWorkflowGuardTests
{
    public static TheoryData<EPostRebaseWhen, bool, int, bool, bool, bool> Combinations()
    {
        var data = new TheoryData<EPostRebaseWhen, bool, int, bool, bool, bool>();
        foreach (var when in Enum.GetValues<EPostRebaseWhen>())
            foreach (var action in new[] { true, false })
                foreach (var resolved in new[] { 0, 2 })
                    foreach (var touches in new[] { true, false })
                        foreach (var fork in new[] { true, false })
                            foreach (var bypass in new[] { true, false })
                                data.Add(when, action, resolved, touches, fork, bypass);
        return data;
    }

    private static MergeQueueConfigSnapshot Config(EPostRebaseWhen when, bool action)
        => new() { PostRebaseWhen = when, PostRebaseActionName = action ? "regenerate.yml" : null };

    /// <summary>The regeneration rule of §5.2: the regenerate workflow is needed when it is configured, and either always
    /// or because a conflict was resolved.</summary>
    private static bool Regenerates(EPostRebaseWhen when, bool action, int resolved)
        => action && (when == EPostRebaseWhen.Always || (when == EPostRebaseWhen.AutoResolved && resolved > 0));

    /// <summary>
    /// Invariant 1 (O25): a dispatch never pushes, for a fork, a tree whose .github differs from the base's. The queue
    /// branch is a branch of this repository, where a push runs the pushed commit's workflow files with its secrets. No
    /// role changes that, and the plan names it as a fork's, so the check run can say so.
    /// </summary>
    [Theory]
    [MemberData(nameof(Combinations))]
    public void A_dispatch_never_pushes_a_tree_whose_github_differs_from_the_bases_for_a_fork(
        EPostRebaseWhen when, bool action, int resolved, bool touches, bool fork, bool bypass)
    {
        var plan = MergeQueueWorkflowGuard.AfterPrepare(Config(when, action), resolved, touches, fork, bypass);
        var because = $"{when}, action {action}, {resolved} resolved, touches {touches}, fork {fork}, bypass {bypass}";

        if (plan == EMergeQueuePlan.Dispatch)
            (fork && touches).Should().BeFalse(because);
        if (fork && touches && Regenerates(when, action, resolved))
            plan.Should().Be(EMergeQueuePlan.EvictForkTouchesWorkflows, because);
        if (!fork)
            plan.Should().NotBe(EMergeQueuePlan.EvictForkTouchesWorkflows, because);
    }

    /// <summary>Invariant 2 (O21 A, O22): a same-repository dispatch carries a .github/** change only through the bypass
    /// role; without it, such a change that needs the dispatch is evicted touches-workflows.</summary>
    [Theory]
    [MemberData(nameof(Combinations))]
    public void A_same_repository_dispatch_carries_a_github_change_only_through_the_bypass_role(
        EPostRebaseWhen when, bool action, int resolved, bool touches, bool fork, bool bypass)
    {
        if (fork) return;
        var plan = MergeQueueWorkflowGuard.AfterPrepare(Config(when, action), resolved, touches, fork, bypass);

        if (touches && Regenerates(when, action, resolved))
            plan.Should().Be(bypass ? EMergeQueuePlan.Dispatch : EMergeQueuePlan.EvictTouchesWorkflows);
        if (plan == EMergeQueuePlan.Dispatch && touches)
            bypass.Should().BeTrue();
    }

    /// <summary>
    /// Invariant 3 (O21 A, O25): without a dispatch, a .github/** change never stops the entry, a fork's included. Nothing
    /// of the pull request reaches a branch of this repository before the merge (§0 D65): the rebased commit goes to the
    /// pull request's own branch, a fork's to the fork, and its CI runs there exactly as it would without the queue.
    /// </summary>
    [Theory]
    [MemberData(nameof(Combinations))]
    public void Without_a_dispatch_a_github_change_never_stops_the_entry_a_forks_included(
        EPostRebaseWhen when, bool action, int resolved, bool touches, bool fork, bool bypass)
    {
        if (Regenerates(when, action, resolved)) return;

        MergeQueueWorkflowGuard.AfterPrepare(Config(when, action), resolved, touches, fork, bypass).Should().Be(EMergeQueuePlan.Publish);
    }

    /// <summary>Without a .github/** change the guard is invisible: the plan is the regeneration rule of §5.2.</summary>
    [Theory]
    [MemberData(nameof(Combinations))]
    public void Without_a_github_change_the_plan_is_the_regeneration_rule(EPostRebaseWhen when, bool action, int resolved, bool touches, bool fork, bool bypass)
    {
        if (touches) return;

        MergeQueueWorkflowGuard.AfterPrepare(Config(when, action), resolved, touches, fork, bypass)
            .Should().Be(Regenerates(when, action, resolved) ? EMergeQueuePlan.Dispatch : EMergeQueuePlan.Publish);
    }

    [Theory]
    [InlineData(false, 0, false, EMergeQueuePlan.Publish)]                    // O21 A: nothing dispatched, so it goes through
    [InlineData(false, 1, false, EMergeQueuePlan.EvictTouchesWorkflows)]      // T5: the dispatch would run it
    [InlineData(false, 1, true, EMergeQueuePlan.Dispatch)]                    // O22: the bypass role
    [InlineData(false, 0, true, EMergeQueuePlan.Publish)]
    [InlineData(true, 0, false, EMergeQueuePlan.Publish)]                     // O25: a fork's too, without a dispatch
    [InlineData(true, 0, true, EMergeQueuePlan.Publish)]
    [InlineData(true, 1, false, EMergeQueuePlan.EvictForkTouchesWorkflows)]   // O25: the queue-branch push would run it
    [InlineData(true, 1, true, EMergeQueuePlan.EvictForkTouchesWorkflows)]    // O25: no role bypasses it
    public void A_github_change_is_stopped_only_by_a_dispatch_and_a_forks_by_no_role(bool fork, int resolved, bool bypass, EMergeQueuePlan expected)
        => MergeQueueWorkflowGuard.AfterPrepare(Config(EPostRebaseWhen.AutoResolved, action: true), resolved, touchesWorkflows: true, fork, bypass)
            .Should().Be(expected);

    [Theory]
    [InlineData(".github/workflows/ci.yml", true)]
    [InlineData(".GitHub/workflows/ci.yml", true)]
    [InlineData(".github/actions/x/action.yml", false)]
    [InlineData("src/.github/workflows/ci.yml", false)]
    [InlineData(null, false)]
    public void Workflow_files_are_the_ones_under_github_workflows(string? path, bool expected)
        => MergeQueueWorkflowGuard.IsWorkflowFile(path).Should().Be(expected);
}
