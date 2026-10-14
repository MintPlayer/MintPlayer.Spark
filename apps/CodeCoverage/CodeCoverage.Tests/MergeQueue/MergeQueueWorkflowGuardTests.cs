using CodeCoverage.Entities;
using CodeCoverage.MergeQueue;
using Xunit;

namespace CodeCoverage.Tests.MergeQueue;

/// <summary>
/// <see cref="MergeQueueWorkflowGuard"/>, the one place that decides whether the regenerate workflow is dispatched (merge-queue
/// PRD §0 D63, D12, O21 A, O22), over every combination of its inputs.
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

    /// <summary>The invariant: a dispatch never runs a .github/** change, unless O22's bypass role lets a same-repository one through.</summary>
    [Theory]
    [MemberData(nameof(Combinations))]
    public void A_dispatch_never_carries_a_github_change_unless_the_bypass_role_lets_a_same_repository_one_through(
        EPostRebaseWhen when, bool action, int resolved, bool touches, bool fork, bool bypass)
    {
        var plan = MergeQueueWorkflowGuard.AfterPrepare(Config(when, action), resolved, touches, fork, bypass);

        if (plan == EMergeQueuePlan.Dispatch)
            (!touches || (bypass && !fork)).Should().BeTrue($"{when}, action {action}, {resolved} resolved, touches {touches}, fork {fork}, bypass {bypass}");
        if (touches && fork)
            plan.Should().Be(EMergeQueuePlan.EvictTouchesWorkflows, "a fork's .github/** change goes nowhere (§0 D63)");
    }

    /// <summary>Without a .github/** change the guard is invisible: the plan is the regeneration rule of §5.2.</summary>
    [Theory]
    [MemberData(nameof(Combinations))]
    public void Without_a_github_change_the_plan_is_the_regeneration_rule(EPostRebaseWhen when, bool action, int resolved, bool touches, bool fork, bool bypass)
    {
        if (touches) return;
        var regenerate = action && (when == EPostRebaseWhen.Always || (when == EPostRebaseWhen.AutoResolved && resolved > 0));

        MergeQueueWorkflowGuard.AfterPrepare(Config(when, action), resolved, touches, fork, bypass)
            .Should().Be(regenerate ? EMergeQueuePlan.Dispatch : EMergeQueuePlan.Publish);
    }

    [Theory]
    [InlineData(0, false, EMergeQueuePlan.Publish)]                // O21 A: nothing dispatched, so it goes through
    [InlineData(1, false, EMergeQueuePlan.EvictTouchesWorkflows)]  // T5: the dispatch would run it
    [InlineData(1, true, EMergeQueuePlan.Dispatch)]                // O22: the bypass role
    [InlineData(0, true, EMergeQueuePlan.Publish)]
    public void A_same_repository_github_change_is_stopped_only_by_a_dispatch(int resolved, bool bypass, EMergeQueuePlan expected)
        => MergeQueueWorkflowGuard.AfterPrepare(Config(EPostRebaseWhen.AutoResolved, action: true), resolved, touchesWorkflows: true, isFork: false, bypass)
            .Should().Be(expected);

    [Fact]
    public void Only_a_same_repository_change_may_be_pushed_to_the_queue_branch_at_all()
    {
        MergeQueueWorkflowGuard.MayPushWorkflowChanges(isFork: false).Should().BeTrue();
        MergeQueueWorkflowGuard.MayPushWorkflowChanges(isFork: true).Should().BeFalse();
    }

    [Theory]
    [InlineData(".github/workflows/ci.yml", true)]
    [InlineData(".GitHub/workflows/ci.yml", true)]
    [InlineData(".github/actions/x/action.yml", false)]
    [InlineData("src/.github/workflows/ci.yml", false)]
    [InlineData(null, false)]
    public void Workflow_files_are_the_ones_under_github_workflows(string? path, bool expected)
        => MergeQueueWorkflowGuard.IsWorkflowFile(path).Should().Be(expected);
}
