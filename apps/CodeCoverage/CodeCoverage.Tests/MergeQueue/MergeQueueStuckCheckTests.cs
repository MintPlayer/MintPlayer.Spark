using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.MergeQueue;
using CodeCoverage.Tests.CommitGraph;
using Xunit;
using static CodeCoverage.Tests.MergeQueue.MergeQueueHarness;

namespace CodeCoverage.Tests.MergeQueue;

/// <summary>
/// #714: a required or expected check that never reports on the queue's commit, which held every queued pull request for
/// the 60-minute timeout after MintPlayer.Spark renamed two CI jobs. Each test names the decision it holds
/// (<c>docs/merge_queue_required_check_renames_PRD.md</c> §5.1).
/// </summary>
public class MergeQueueStuckCheckTests : CoverageRavenTest
{
    private static readonly DateTimeOffset Start = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);
    private const string ChecksRuleset = "Reviews and checks";
    private const long GitHubActions = 15368;

    private async Task<(MergeQueueHarness H, MutableTime Time)> HarnessAsync(string mergeBase = BaseSha)
    {
        var time = new MutableTime(Start);
        var h = new MergeQueueHarness(GetDocumentStore(), mergeBase) { Time = time };
        await h.SeedAsync();
        return (h, time);
    }

    /// <summary>Rules whose ruleset 2 requires <paramref name="checks"/>, as GitHub's branch rules answer them.</summary>
    private static ForgeBranchRules Requiring(params ForgeRequiredCheck[] checks)
        => new(true, [.. checks.Select(check => check.Context)], 0, false, false, false, false,
            [new ForgeRuleset(2, ChecksRuleset, ["pull_request", "required_status_checks"], EForgeRulesetBypass.Never, checks)],
            EForgeClassicProtection.None);

    private static ForgeBranchRules RequiringNames(params string[] names) => Requiring([.. names.Select(name => new ForgeRequiredCheck(name))]);

    /// <summary>One more look a poll interval later, which is what makes a stuck wait "held" (S3's condition 4).</summary>
    private static async Task PollAsync(MergeQueueHarness h, MutableTime time)
    {
        time.Now += TimeSpan.FromMinutes(2);
        await h.ReleaseAsync(EMergeQueueStep.PollChecks);
    }

    private async Task<RulesetNotice?> NoticeAsync(MergeQueueHarness h, ERulesetNoticeKind kind)
    {
        using var session = h.Store.OpenAsyncSession();
        return await session.LoadAsync<RulesetNotice>(RulesetNotice.DocumentId(RepositoryId.ToLowerInvariant(), kind));
    }

    // ── C1, C3: the wait names what it waits for ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_required_check_that_never_reported_is_named_with_the_ruleset_that_requires_it()
    {
        var (h, _) = await HarnessAsync();
        h.Queue.Rules = RequiringNames("test");

        await h.EnqueueAsync();
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.AwaitingChecks, "Dec7: no early eviction, a late workflow may still report");
        entry.StuckChecks.Should().ContainSingle().Which.Source().Should().Be("required by ruleset 'Reviews and checks' (2)");
        entry.StuckSinceUtc.Should().Be(Start.UtcDateTime);
        var check = h.Queue.QueueChecks.Last().Check;
        check.Title.Should().Be(MergeQueueCheckRenderer.StuckTitle);
        check.Summary.Should().Contain("- `test`: required by ruleset 'Reviews and checks' (2)").And.Contain("Set up rulesets");
        h.Queue.Comments.Should().BeEmpty("one look is not enough: a workflow that workflow_run starts shows its suite late (S3)");
        (await NoticeAsync(h, ERulesetNoticeKind.MissingCheck)).Should().BeNull("Dec5 waits for the second look too");
    }

    [Fact]
    public async Task A_check_still_running_is_not_stuck()
    {
        var (h, _) = await HarnessAsync();
        h.Queue.Rules = RequiringNames("test");
        h.Queue.CheckStates[Head] = Pending(Head);

        await h.EnqueueAsync();
        await h.DrainAsync();

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.AwaitingChecks);
        entry.StuckChecks.Should().BeEmpty("`build` is still running, and `test` may be a later job of its workflow");
        entry.StuckSinceUtc.HasValue.Should().BeFalse();
        h.Queue.QueueChecks.Last().Check.Title.Should().Be("Waiting for checks");
    }

    // ── Dec8, Dec5: one comment and one notice, once the wait held ──────────────────────────────────────────

    [Fact]
    public async Task A_wait_that_held_a_poll_comments_once_and_writes_down_a_notice_for_the_managers()
    {
        var (h, time) = await HarnessAsync();
        h.Queue.Rules = RequiringNames("test");
        await h.EnqueueAsync();
        await h.DrainAsync();

        await PollAsync(h, time);

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.AwaitingChecks);
        entry.StuckCommented.Should().BeTrue();
        h.Queue.Comments.Should().ContainSingle().Which.Body.Should()
            .Contain("haven't reported").And.NotContain("doesn't exist", "the queue never reads the workflow files (Dec8)")
            .And.Contain("- `test`: required by ruleset 'Reviews and checks' (2)")
            .And.Contain("Set up rulesets")
            .And.NotContain("This pull request changes workflow files", "D3's hint is for a pull request that changes them")
            .And.Contain("2026-11-27 10:00 UTC", "the 60-minute timeout from the start of the wait");

        var notice = (await NoticeAsync(h, ERulesetNoticeKind.MissingCheck))!;
        notice.Should().NotBeNull();
        notice.IsOpen().Should().BeTrue();
        notice.BaseBranch.Should().Be("master");
        notice.Checks.Should().Equal(["test"]);
        notice.ReportedOnHead.Should().Equal(["build"], "Dec6: what the setup offers as the summary job, guessing nothing");
        notice.PullRequest.Should().Be(Number);
        notice.NotifiedUsers.Should().BeEmpty("the bell delivers it at sign-in");

        await PollAsync(h, time);
        h.Queue.Comments.Should().ContainSingle("Dec8: one comment per stuck entry");
    }

    [Fact]
    public async Task A_required_check_this_pull_request_renamed_gets_the_workflow_hint()
    {
        var (h, time) = await HarnessAsync(mergeBase: OldBase);
        h.Queue.Rules = RequiringNames("test");
        h.Forge.Comparison = new CommitComparison(OldBase, [new DiffFile(".github/workflows/ci.yml", "modified", null, [])], false);
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared) with
        {
            TouchesWorkflows = true,
            Paths = [".github/workflows/ci.yml"],
        });
        h.Writer.PublishResults.Enqueue(FakeGitWriter.Published(Final));
        await h.EnqueueAsync();
        await h.DrainAsync();

        await PollAsync(h, time);

        h.Queue.Comments.Should().ContainSingle().Which.Body.Should().Contain("This pull request changes workflow files", "D3's hint");
    }

    [Fact]
    public async Task A_rename_on_a_pull_request_already_on_its_base_gets_the_workflow_hint_too()
    {
        // S2 on the sandbox (2026-11-28): a rename pull request is usually up to date with its base, so the queue merges its
        // head as it is (O20 case 1) and never runs the prepare, which is what reported the workflow paths.
        var (h, time) = await HarnessAsync();
        h.Queue.Rules = RequiringNames("test");
        h.Forge.Comparison = new CommitComparison(BaseSha, [new DiffFile(".github/workflows/ci.yml", "modified", null, [])], false);
        await h.EnqueueAsync();
        await h.DrainAsync();

        await PollAsync(h, time);

        (await h.EntryAsync()).DirectMerge.Should().BeTrue();
        h.Queue.Comments.Should().ContainSingle().Which.Body.Should().Contain("This pull request changes workflow files", "D3's hint");
    }

    // ── C2 (Dec7): a fixed ruleset frees the entry at the next look ─────────────────────────────────────────

    [Fact]
    public async Task Rules_fixed_while_the_entry_waits_free_it_at_the_next_look_and_resolve_the_notice()
    {
        var (h, time) = await HarnessAsync();
        h.Queue.Rules = RequiringNames("test");
        await h.EnqueueAsync();
        await h.DrainAsync();
        await PollAsync(h, time);
        (await NoticeAsync(h, ERulesetNoticeKind.MissingCheck))!.IsOpen().Should().BeTrue();

        // Someone ran "Set up rulesets", or fixed the ruleset by hand: GitHub tells an App without Administration nothing.
        h.Queue.Rules = RequiringNames("build");
        await PollAsync(h, time);

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Merged);
        entry.RequiredChecks.Should().Equal(["build"]);
        entry.History.Should().Contain(e => e.Text == "the branch's required checks changed: added `build`; dropped `test`");
        (await NoticeAsync(h, ERulesetNoticeKind.MissingCheck))!.IsOpen().Should().BeFalse("the rules no longer require `test`");
    }

    [Fact]
    public async Task An_expected_check_alone_does_not_read_the_rules_again()
    {
        var (h, time) = await HarnessAsync();
        h.Queue.CheckNames[Head] = ["build", "lint"];
        await h.EnqueueAsync();
        await h.DrainAsync();
        var reads = h.Queue.RuleReads;

        await PollAsync(h, time);

        h.Queue.RuleReads.Should().Be(reads, "only a required name can be freed by the rules (C2's cost)");
        var entry = await h.EntryAsync();
        entry.StuckChecks.Should().ContainSingle().Which.Source().Should().Be("reported on the queued head");
        h.Queue.Comments.Should().ContainSingle().Which.Body.Should().Contain("waited for until it reports on the queue's commit too");
        (await NoticeAsync(h, ERulesetNoticeKind.MissingCheck)).Should().BeNull("Dec5: only a rule's drift is for the managers");
    }

    // ── C4 (Dec9): an expected name the base's workflow change removed ─────────────────────────────────────

    /// <summary>
    /// S3's four conditions: the base changed its workflows, a GitHub Actions run reported the name on the queued head, the
    /// gate is stuck, and it was stuck a poll interval earlier too.
    /// </summary>
    [Fact]
    public async Task An_expected_workflow_job_the_base_removed_is_dropped_once_the_wait_held()
    {
        var (h, time) = await HarnessAsync(mergeBase: OldBase);
        h.Queue.CheckNames[Head] = ["build", "lint"];
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared) with { BaseChangedWorkflows = true });
        h.Writer.PublishResults.Enqueue(FakeGitWriter.Published(Final));
        await h.EnqueueAsync();
        await h.DrainAsync();

        var waiting = await h.EntryAsync();
        waiting.State.Should().Be(EMergeQueueEntryState.AwaitingChecks, "one look is not enough (S3's condition 4)");
        waiting.ExpectedCheckApps.Should().Contain("lint", CheckGate.WorkflowRunsAppSlug);

        await PollAsync(h, time);

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Merged);
        entry.DroppedExpectedChecks.Should().Equal(["lint"]);
        entry.ExpectedChecks.Should().Equal(["build"]);
        entry.History.Should().Contain(e => e.Text.StartsWith("no longer waiting for `lint`", StringComparison.Ordinal));
        h.Queue.Comments.Should().BeEmpty("it was freed before anything had to be said");
    }

    [Theory]
    [InlineData("base-unchanged")]
    [InlineData("other-app")]
    [InlineData("status")]
    [InlineData("required")]
    public async Task An_expected_name_is_kept_unless_all_four_conditions_hold(string why)
    {
        var (h, time) = await HarnessAsync(mergeBase: OldBase);
        h.Queue.ChecksOnCommit[Head] =
        [
            new ForgeReportedCheck("build", EForgeCheckKind.CheckRun, CheckGate.WorkflowRunsAppSlug, OnPullRequest: true, AppId: GitHubActions),
            why switch
            {
                "other-app" => new ForgeReportedCheck("lint", EForgeCheckKind.CheckRun, "gitguardian", OnPullRequest: true, AppId: 46505),
                "status" => new ForgeReportedCheck("lint", EForgeCheckKind.Status, null, OnPullRequest: true),
                _ => new ForgeReportedCheck("lint", EForgeCheckKind.CheckRun, CheckGate.WorkflowRunsAppSlug, OnPullRequest: true, AppId: GitHubActions),
            },
        ];
        if (why == "required") h.Queue.Rules = RequiringNames("build", "lint");
        h.Writer.PrepareResults.Enqueue(FakeGitWriter.Prepared(BaseSha, Head, Prepared) with { BaseChangedWorkflows = why != "base-unchanged" });
        h.Writer.PublishResults.Enqueue(FakeGitWriter.Published(Final));
        await h.EnqueueAsync();
        await h.DrainAsync();

        await PollAsync(h, time);

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.AwaitingChecks, $"{why}: the queue keeps waiting, and the timeout stays (Dec7)");
        entry.DroppedExpectedChecks.Should().BeEmpty();
        entry.StuckChecks.Select(m => m.Name).Should().Equal(["lint"]);
        h.Queue.Comments.Should().ContainSingle().Which.Body.Should().Contain("`lint`");
    }

    // ── Dec7: the timeout names what never reported ─────────────────────────────────────────────────────────

    [Fact]
    public async Task The_timeout_of_a_stuck_wait_names_what_never_reported()
    {
        var (h, time) = await HarnessAsync();
        h.Queue.Rules = RequiringNames("test");
        await h.EnqueueAsync();
        await h.DrainAsync();
        await PollAsync(h, time);

        time.Now = Start.AddMinutes(60);
        await h.ReleaseAsync(EMergeQueueStep.Timeout);

        var entry = await h.EntryAsync();
        entry.EvictionReason.Should().Be("checks-timeout");
        entry.EvictionMessage.Should().Contain("never reported within 60 minutes").And.NotContain("did not finish")
            .And.Contain("- `test`: required by ruleset 'Reviews and checks' (2)");
    }

    // ── Dec4, Dec5: a check pinned to another App ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_merge_refused_over_an_App_pin_names_the_App_and_writes_down_a_notice()
    {
        var (h, _) = await HarnessAsync();
        h.Queue.Rules = Requiring(new ForgeRequiredCheck("build", 999));
        h.Queue.CheckStates[Head] = new ForgeCheckState(Head,
            [new ForgeCheckRun(41, "build", "completed", "success", 11, "github-actions", null, AppId: GitHubActions)], [],
            [new ForgeCheckSuite(11, "completed", "success", "github-actions", 1)]);
        h.Queue.MergeReplies.Enqueue(new ForgeMergeRequest(EForgeMergeState.Pending, "d45ff22b-73cf-44a4-9eb0-cbf81c955578", null, "enqueued"));
        h.Queue.MergeStatuses.Enqueue(new ForgeMergeRequest(EForgeMergeState.Failed, null, null, "Repository rule violations found\n\nRequired status check \"build\" is expected."));

        await h.EnqueueAsync();
        await h.DrainAsync();
        await h.ReleaseAsync(EMergeQueueStep.PollMerge);

        var entry = await h.EntryAsync();
        entry.EvictionReason.Should().Be("merge-refused");
        entry.EvictionMessage.Should().Contain("`build` is required from another App than the one that reported it (App 15368)")
            .And.NotContain("approval of the most recent push");
        var notice = (await NoticeAsync(h, ERulesetNoticeKind.PinMismatch))!;
        notice.Should().NotBeNull();
        notice.ReportingApps.Should().HaveCount(1);
        notice.ReportingApps["build"].Should().Be(GitHubActions);
        notice.PullRequest.Should().Be(Number);
    }

    // ── Dec11: one check per job is offered the summary job (once: RulesetNoticesTests) ────────────────────

    [Fact]
    public async Task Rules_that_require_one_check_per_job_are_offered_the_summary_job_at_enqueue()
    {
        var (h, _) = await HarnessAsync();
        h.Queue.Rules = RequiringNames("build", MergeQueueRulesetPlanner.LinePolicyCheckName, "test");
        h.Queue.CheckStates[Head] = new ForgeCheckState(Head,
            [
                new ForgeCheckRun(41, "build", "completed", "success", 11, "github-actions", null),
                new ForgeCheckRun(44, "test", "completed", "success", 11, "github-actions", null),
                new ForgeCheckRun(45, MergeQueueRulesetPlanner.LinePolicyCheckName, "completed", "success", 12, "codecoverage", null),
            ], [],
            [new ForgeCheckSuite(11, "completed", "success", "github-actions", 2), new ForgeCheckSuite(12, "completed", "success", "codecoverage", 1)]);

        await h.EnqueueAsync();
        await h.DrainAsync();

        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Merged);
        var offer = (await NoticeAsync(h, ERulesetNoticeKind.SummaryJob))!;
        offer.Should().NotBeNull();
        offer.Checks.Should().Equal(["build", "test"], "the App's own checks are not jobs");
        offer.BaseBranch.Should().Be("master");
        offer.IsOpen().Should().BeTrue();
    }
}
