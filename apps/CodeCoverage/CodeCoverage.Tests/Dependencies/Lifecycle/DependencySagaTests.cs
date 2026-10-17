using System.Text;
using System.Text.Json.Nodes;
using CodeCoverage.Dependencies.Lifecycle;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Git.Dependencies;
using CodeCoverage.MergeQueue;
using CodeCoverage.Tests.Dependencies.Policy;
using CodeCoverage.Tests.MergeQueue;
using Xunit;
using static CodeCoverage.Tests.Dependencies.Lifecycle.DependencyHarness;

namespace CodeCoverage.Tests.Dependencies.Lifecycle;

/// <summary>
/// The dependency saga (dependency-updates PRD §13.8 N5–N7) with the forge and the git writer scripted and the run state in a
/// real RavenDB, like the merge queue's engine tests. Each test names the decision it holds (§13.11.1).
/// </summary>
public class DependencySagaTests : CoverageRavenTest
{
    private async Task<DependencyHarness> HarnessAsync(bool enabled = true, int bisectBudget = 0, bool secondPullRequest = false)
    {
        var harness = new DependencyHarness(GetDocumentStore());
        await harness.SeedAsync(enabled, bisectBudget);
        if (secondPullRequest)
        {
            // Past onboarding: the first pull request already happened, so the next one is not a draft (R16).
            await harness.StoreAsync(new DependencyUpdateRun
            {
                Id = DependencyUpdateRun.ForRepository(RepositoryId), Repository = RepositoryId, FirstPullRequestOpened = true, Onboarding = EDependencyOnboarding.Done,
            }, DependencyUpdateRun.ForRepository(RepositoryId));
        }
        return harness;
    }

    private static Git.Dependencies.LookupDocument Chalk() => Lookup(Npm("chalk", "4.1.0", "4.1.2", Git.Dependencies.EUpdateType.Patch));

    private static Git.Dependencies.LookupDocument ChalkAndLodash()
        => Lookup(Npm("chalk", "4.1.0", "4.1.2", Git.Dependencies.EUpdateType.Patch), Npm("lodash", "4.16.0", "4.17.21"));

    /// <summary>Lookup → plan commit → apply → validate → rolling → pull request → CI, all the way to the checks wait.</summary>
    private static async Task PublishAsync(DependencyHarness h, Git.Dependencies.LookupDocument lookup)
    {
        await h.RunToApplyAsync(lookup);
        await h.FinishApplyAsync();
    }

    // ── R3: two one-way dispatches ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task R3_a_scheduled_run_dispatches_the_lookup_then_the_apply_on_the_plan_commit_and_publishes()
    {
        var h = await HarnessAsync();

        await h.RunToApplyAsync(Chalk());

        h.Queue.Dispatches[0].Workflow.Should().Be("dependency-lookup.yml");
        h.Queue.Dispatches[0].Branch.Should().Be("master");
        h.Queue.Dispatches[0].Inputs["expected_sha"].Should().Be(BaseSha);
        var planCommit = h.Writer.Requests("work-ref").Should().ContainSingle().Which;
        planCommit.ExpectedSha.Should().Be(BaseSha);
        planCommit.Patch.Should().BeNull("R14: the plan commit carries the plan file only");
        DependencyContract.Deserialize<DependencyPlan>(planCommit.PlanJson!)!.Groups.Select(g => g.Group).Should().Equal(["chalk"]);
        h.Queue.Dispatches[1].Workflow.Should().Be("dependency-apply.yml");
        h.Queue.Dispatches[1].Branch.Should().Be(DependencyUpdateRun.WorkBranch);
        h.Queue.Dispatches[1].Inputs["expected_sha"].Should().Be(BaseSha);
        (await h.RunAsync()).State.Should().Be(EDependencyRunState.Applying);

        await h.FinishApplyAsync();

        var validate = h.Writer.Requests("validate").Should().ContainSingle().Which;
        validate.Patch.Should().Be(Convert.ToBase64String(Encoding.UTF8.GetBytes(DependencyHarness.Patch)));
        validate.ClaimedGroups.Should().Equal(["chalk"]);
        var rolling = h.Writer.Requests("rolling").Should().ContainSingle().Which;
        rolling.ExpectedRollingSha.Should().BeNull();
        rolling.LastBotSha.Should().BeNull();
        var run = await h.RunAsync();
        run.State.Should().Be(EDependencyRunState.AwaitingChecks);
        run.RollingSha.Should().Be(h.Writer.RollingSha);
        run.RollingBotSha.Should().Be(h.Writer.BotSha);
        run.PullRequestNumber.Should().Be(h.Updates.LastPull.Number);
    }

    [Fact]
    public async Task R3_a_base_that_moved_only_in_docs_keeps_the_plan()
    {
        var h = await HarnessAsync();
        await h.RunToApplyAsync(Chalk());
        h.Queue.BranchHeads["master"] = NewBase;
        h.Forge.Comparison = new CommitComparison(BaseSha, [new DiffFile("docs/readme.md", "modified", null, [])], false);

        await h.FinishApplyAsync();

        h.Writer.Requests("rolling").Should().ContainSingle();
        (await h.RunAsync()).History.Should().Contain(e => e.Text.Contains("the plan stands"));
    }

    [Fact]
    public async Task R3_a_base_that_moved_in_a_manifest_discards_the_plan_and_looks_up_again()
    {
        var h = await HarnessAsync();
        await h.RunToApplyAsync(Chalk());
        h.Queue.BranchHeads["master"] = NewBase;
        h.Forge.Comparison = new CommitComparison(BaseSha, [new DiffFile("package.json", "modified", null, [1])], false);

        await h.FinishApplyAsync();

        h.Writer.Requests("validate").Should().BeEmpty("the apply ran on a plan the base no longer matches");
        h.Writer.Requests("rolling").Should().BeEmpty();
        h.Queue.Dispatches.Should().HaveCount(3);
        h.Queue.Dispatches[2].Workflow.Should().Be("dependency-lookup.yml");
        h.Queue.Dispatches[2].Inputs["expected_sha"].Should().Be(NewBase);
    }

    [Theory]
    [InlineData("failure", "lookup-failed")]
    [InlineData("cancelled", "lookup-failed")]
    public async Task R3_a_red_lookup_dispatches_no_apply(string conclusion, string outcome)
    {
        var h = await HarnessAsync();
        h.ScriptRun(LookupRunId, BaseSha, conclusion);

        await h.TriggerAsync();
        await h.DrainAsync();
        await h.ReleaseAsync(EDependencyStep.PollRun);

        h.Queue.Dispatches.Should().ContainSingle();
        h.Writer.Requests("work-ref").Should().BeEmpty();
        var run = await h.RunAsync();
        run.State.Should().Be(EDependencyRunState.Idle);
        run.Outcome.Should().Be(outcome);
    }

    [Fact]
    public async Task R3_an_uncorrelated_lookup_dispatch_is_not_waited_for()
    {
        var h = await HarnessAsync();
        h.Queue.DispatchQueue.Enqueue(new ForgeWorkflowDispatch(EForgeDispatchOutcome.Dispatched, null, null, null));

        await h.TriggerAsync();
        await h.DrainAsync();

        (await h.RunAsync()).Outcome.Should().Be("dispatch-uncorrelated");
        h.Writer.Requests("work-ref").Should().BeEmpty();
    }

    [Fact]
    public async Task R3_a_lookup_that_times_out_is_cancelled_and_dispatches_no_apply()
    {
        var h = await HarnessAsync();
        h.ScriptRun(LookupRunId, BaseSha, conclusion: null);

        await h.TriggerAsync();
        await h.ReleaseAsync(EDependencyStep.Timeout);

        (await h.RunAsync()).Outcome.Should().Be("lookup-timeout");
        h.Queue.CancelledRuns.Should().Contain(LookupRunId);
        h.Writer.Requests("work-ref").Should().BeEmpty();
    }

    // ── R14: the plan commit and the work ref ──────────────────────────────────────────────────────

    [Fact]
    public async Task R14_the_work_ref_is_deleted_after_a_published_run_under_a_lease_on_the_plan_commit()
    {
        var h = await HarnessAsync();

        await PublishAsync(h, Chalk());

        h.Writer.Requests("delete").Should().ContainSingle().Which.ExpectedSha.Should().Be(h.Writer.PlanCommitSha);
    }

    [Fact]
    public async Task R14_the_work_ref_is_deleted_after_a_failed_apply()
    {
        var h = await HarnessAsync();
        await h.RunToApplyAsync(Chalk());
        h.Queue.Runs[ApplyRunId] = h.Queue.Runs[ApplyRunId] with { Conclusion = "failure" };

        await h.ReleaseAsync(EDependencyStep.PollRun);

        (await h.RunAsync()).Outcome.Should().Be("apply-failed");
        h.Writer.Requests("delete").Should().ContainSingle().Which.ExpectedSha.Should().Be(h.Writer.PlanCommitSha);
        h.Writer.Requests("rolling").Should().BeEmpty();
    }

    [Fact]
    public async Task R14_a_refused_patch_publishes_nothing()
    {
        var h = await HarnessAsync();
        h.Writer.Script("validate", new GitWriterDependencyResult(GitWriterDependencyOutcomes.Validated, "not-a-substitution", null, null, ["package.json"], "Line 3", 5)
        {
            Verdict = GitWriterDependencyOutcomes.Rejected,
        });

        await PublishAsync(h, Chalk());

        (await h.RunAsync()).Outcome.Should().Be("patch-rejected");
        h.Writer.Requests("rolling").Should().BeEmpty();
        h.Updates.Created.Should().BeEmpty();
    }

    // ── R4: rebuild and replay ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task R4_the_next_run_rebuilds_on_a_lease_and_replays_above_the_bot_commit()
    {
        var h = await HarnessAsync();
        await PublishAsync(h, Chalk());
        h.Queue.BranchHeads[DependencyUpdateRun.RollingBranch] = h.Writer.RollingSha;

        h.ScriptApply(runId: 6002);
        h.ScriptLookup(Chalk(), runId: 6001);
        h.Queue.DispatchQueue.Enqueue(new ForgeWorkflowDispatch(EForgeDispatchOutcome.Dispatched, 6002, null, null));
        await h.TriggerAsync();
        await h.ReleaseAsync(EDependencyStep.PollRun);
        await h.FinishApplyAsync();

        var second = h.Writer.Requests("rolling")[^1];
        second.ExpectedRollingSha.Should().Be(h.Writer.RollingSha);
        second.LastBotSha.Should().Be(h.Writer.BotSha);
        h.Updates.Created.Should().ContainSingle("the second run updates the same pull request");
        h.Updates.Updated.Should().Contain(u => u.Number == h.Updates.LastPull.Number);
    }

    [Fact]
    public async Task R4_an_equal_tree_pushes_nothing()
    {
        var h = await HarnessAsync();
        h.Writer.Script("rolling", new GitWriterDependencyResult(GitWriterDependencyOutcomes.Unchanged, null, "9999999999999999999999999999999999999999", null, [], null, 5));

        await PublishAsync(h, Chalk());

        var run = await h.RunAsync();
        run.RollingSha.Should().Be("9999999999999999999999999999999999999999");
        run.History.Should().Contain(e => e.Text.Contains("nothing pushed, no CI"));
    }

    [Fact]
    public async Task R4_a_human_manifest_value_becomes_a_hold()
    {
        var h = await HarnessAsync();
        h.Writer.Script("rolling", new GitWriterDependencyResult(GitWriterOutcomes.Pushed, null, h.Writer.RollingSha, h.Writer.BotSha, [], null, 5)
        {
            HumanValues = [new GitWriterHumanValue("chalk", "package.json", "4.1.1", "4.1.2", "abcdefabcdefabcdefabcdefabcdefabcdefabcd", "octocat")],
            Replayed = 1,
        });

        await PublishAsync(h, Chalk());

        var hold = (await h.StateAsync()).Holds.Should().ContainSingle(x => x.Id == "human:chalk").Which;
        hold.HeldValue.Should().Be("4.1.1");
        hold.ProposedVersion.Should().Be("4.1.2");
        hold.By.Should().Be("octocat");
    }

    [Fact]
    public async Task R4_a_replay_conflict_keeps_the_branch_and_ends_the_run()
    {
        var h = await HarnessAsync();
        h.Writer.Script("rolling", new GitWriterDependencyResult(GitWriterDependencyOutcomes.ReplayConflict, null, h.Writer.RollingSha, null, ["package-lock.json"], "conflicts", 5));

        await PublishAsync(h, Chalk());

        (await h.RunAsync()).Outcome.Should().Be(GitWriterDependencyOutcomes.ReplayConflict);
        h.Updates.Created.Should().BeEmpty();
    }

    // ── D-N1: a human fix keeps its versions; D-N2: lockfile conflicts are regenerated ───────────────

    [Fact]
    public async Task D_N1_a_human_code_fix_on_the_rolling_branch_keeps_the_versions_it_was_made_against()
    {
        var h = await HarnessAsync();
        await PublishAsync(h, Chalk());
        const string fixedHead = "f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0";
        h.Queue.BranchHeads[DependencyUpdateRun.RollingBranch] = fixedHead;
        h.Forge.Comparison = new CommitComparison(h.Writer.BotSha, [new DiffFile("src/index.ts", "modified", null, [3])], false);

        h.ScriptApply(runId: 6002);
        h.ScriptLookup(Lookup(Npm("chalk", "4.1.0", "4.1.3", Git.Dependencies.EUpdateType.Patch)), runId: 6001);
        h.Queue.DispatchQueue.Enqueue(new ForgeWorkflowDispatch(EForgeDispatchOutcome.Dispatched, 6002, null, null));
        await h.TriggerAsync();
        await h.ReleaseAsync(EDependencyStep.PollRun);

        var plan = DependencyContract.Deserialize<DependencyPlan>(h.Writer.Requests("work-ref")[^1].PlanJson!)!;
        plan.Groups.Single().Items.Single().NewVersion.Should().Be("4.1.2", "the fix in src/index.ts was written against chalk 4.1.2");
        var run = await h.RunAsync();
        run.Pins.Should().Equal(["chalk 4.1.2 (package.json)"]);

        await h.FinishApplyAsync();
        h.Updates.Updated[^1].Body.Should().Contain("## Pinned for a fix on this branch");
    }

    [Fact]
    public async Task D_N2_a_lockfile_conflict_in_the_replay_is_regenerated_by_the_apply_workflow_before_the_push()
    {
        var h = await HarnessAsync();
        const string replayed = "7777777777777777777777777777777777777777", relockPlanCommit = "8888888888888888888888888888888888888888",
            dependencyCommit = "e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1";
        h.Writer.Script("rolling", new GitWriterDependencyResult(GitWriterDependencyOutcomes.RelockNeeded, null, replayed, dependencyCommit, ["package-lock.json"], null, 5)
        {
            Verdict = GitWriterDependencyOutcomes.Accepted, Replayed = 1,
        });
        h.Writer.Script("work-ref", new GitWriterDependencyResult(GitWriterOutcomes.Pushed, null, h.Writer.PlanCommitSha, null, [], null, 5));
        h.Writer.Script("work-ref", new GitWriterDependencyResult(GitWriterOutcomes.Pushed, null, relockPlanCommit, null, [], null, 5));
        // The relock run is dispatched on the relock plan commit, so its apply workflow's provenance is checked there too.
        h.Queue.Blobs[$"{relockPlanCommit}:.github/workflows/dependency-apply.yml"] = "blob-dependency-apply.yml";

        await h.RunToApplyAsync(Chalk());
        // The relock run is dispatched after the apply (the lookup and the apply took the dispatches queued before).
        h.Queue.DispatchQueue.Enqueue(new ForgeWorkflowDispatch(EForgeDispatchOutcome.Dispatched, 7002, null, null));
        h.ScriptRun(7002, relockPlanCommit, branch: DependencyUpdateRun.WorkBranch, dispatch: false);
        await h.FinishApplyAsync();

        var relockCommit = h.Writer.Requests("work-ref")[^1];
        relockCommit.ExpectedSha.Should().Be(replayed, "the relock plan goes on top of the replayed head");
        var relock = DependencyContract.Deserialize<DependencyPlan>(relockCommit.PlanJson!)!;
        relock.Groups.Select(g => g.Group).Should().Equal(["relock:package-lock.json"]);
        h.Queue.Dispatches[^1].Inputs["expected_sha"].Should().Be(replayed);
        (await h.RunAsync()).State.Should().Be(EDependencyRunState.Applying);
        h.Updates.Created.Should().BeEmpty("nothing is pushed or opened before the lockfile is regenerated");

        const string relockPatch = "diff --git a/package-lock.json b/package-lock.json\n";
        h.Updates.Artifacts[7002] = new()
        {
            ["result.json"] = Encoding.UTF8.GetBytes(DependencyContract.Serialize(Result(relock, relockPatch, []))),
            ["dependency.patch"] = Encoding.UTF8.GetBytes(relockPatch),
        };
        await h.ReleaseAsync(EDependencyStep.PollRun);

        var push = h.Writer.Requests("rolling")[^1];
        push.ExpectedSha.Should().Be(replayed);
        push.LastBotSha.Should().BeNull("the replay is done; the relock commit goes on top");
        push.ClaimedGroups.Should().Equal(["relock:package-lock.json"]);
        RollingBranchReplay.IsRelockCommit(push.Message!).Should().BeTrue("a later replay skips the relock commit");
        var run = await h.RunAsync();
        run.State.Should().Be(EDependencyRunState.AwaitingChecks);
        run.RollingBotSha.Should().Be(dependencyCommit, "the next replay starts above the dependency commit, not the relock commit");
        run.RelockBaseSha.Should().BeNull();
        h.Updates.Created.Should().ContainSingle();
    }

    [Fact]
    public async Task D_N2_a_replay_conflict_whose_values_just_became_holds_is_retried_once_at_once()
    {
        var h = await HarnessAsync();
        h.Writer.Script("rolling", new GitWriterDependencyResult(GitWriterDependencyOutcomes.ReplayConflict, null, h.Writer.RollingSha, null, ["package-lock.json"], "conflicts", 5)
        {
            HumanValues = [new GitWriterHumanValue("chalk", "package.json", "4.1.1", "4.1.2", "abcdefabcdefabcdefabcdefabcdefabcdefabcd", "octocat")],
        });

        await PublishAsync(h, Chalk());

        var run = await h.RunAsync();
        run.History.Should().Contain(e => e.Text.Contains("replay-retry"));
        run.ReplayRetried.Should().BeTrue();
        run.Run.Should().Be(2, "the refresh starts at once, planning without the held chalk");
        (await h.StateAsync()).Holds.Should().Contain(x => x.Id == "human:chalk");
    }

    // ── R16: the first pull request is a draft ────────────────────────────────────────────────────

    [Fact]
    public async Task R16_the_first_rolling_pull_request_is_a_draft_and_is_never_enqueued()
    {
        var h = await HarnessAsync();
        await PublishAsync(h, Chalk());
        h.Queue.CheckStates[h.Writer.RollingSha] = Green(h.Writer.RollingSha);

        await h.ReleaseAsync(EDependencyStep.PollChecks);

        h.Updates.Created.Should().ContainSingle().Which.Draft.Should().BeTrue();
        h.Raised.OfType<MergeQueueSignalMessage>().Should().BeEmpty();
        (await h.RunAsync()).Outcome.Should().Be("green");
    }

    // ── R5, R11: enqueue on green ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task R5_green_with_the_queue_on_enqueues_the_pull_request_through_the_merge_queue()
    {
        var h = await HarnessAsync(secondPullRequest: true);
        await PublishAsync(h, Chalk());
        h.Queue.CheckStates[h.Writer.RollingSha] = Green(h.Writer.RollingSha);

        await h.ReleaseAsync(EDependencyStep.PollChecks);

        var signal = h.Raised.OfType<MergeQueueSignalMessage>().Should().ContainSingle().Which;
        signal.Kind.Should().Be(EMergeQueueSignal.Command);
        signal.Command.Should().Be(EMergeQueueCommand.Enqueue);
        signal.Source.Should().Be(EMergeQueueCommandSource.DependencyUpdates);
        signal.Number.Should().Be(h.Updates.LastPull.Number);
        h.Updates.Created.Single().Draft.Should().BeFalse();
    }

    [Fact]
    public async Task R5_green_with_the_queue_off_enqueues_nothing()
    {
        var h = await HarnessAsync(secondPullRequest: true);
        h.SetYml("dependencies:\n  enabled: true\n");
        await PublishAsync(h, Chalk());
        h.Queue.CheckStates[h.Writer.RollingSha] = Green(h.Writer.RollingSha);

        await h.ReleaseAsync(EDependencyStep.PollChecks);

        h.Raised.OfType<MergeQueueSignalMessage>().Should().BeEmpty();
        h.Updates.Updated[^1].Body.Should().Contain("needs the merge queue");
    }

    [Fact]
    public async Task R5_nothing_reported_yet_is_not_green()
    {
        var h = await HarnessAsync(secondPullRequest: true);
        await PublishAsync(h, Chalk());

        await h.ReleaseAsync(EDependencyStep.PollChecks);

        (await h.RunAsync()).State.Should().Be(EDependencyRunState.AwaitingChecks);
        h.Raised.OfType<MergeQueueSignalMessage>().Should().BeEmpty();
    }

    [Theory]
    [InlineData(-0.2, null, true)]
    [InlineData(-1.0, null, false)]
    [InlineData(-0.1, CommitAssembly.Partial, false)]
    public async Task R11_the_coverage_delta_gates_the_enqueue(double delta, string? completeness, bool enqueued)
    {
        var h = await HarnessAsync(secondPullRequest: true);
        await PublishAsync(h, Chalk());
        h.Queue.CheckStates[h.Writer.RollingSha] = Green(h.Writer.RollingSha);
        await h.StoreAsync(new Commit
        {
            Coverage = new CoverageSummary { LinesCovered = 80, LinesCoverable = 100 },
            CoverageDeltaVsDefaultBranch = delta,
            AssemblyCompleteness = completeness,
        }, Commit.DocumentId(EForgeProvider.GitHub, RepoGitHubId, h.Writer.RollingSha));

        await h.ReleaseAsync(EDependencyStep.PollChecks);

        h.Raised.OfType<MergeQueueSignalMessage>().Any().Should().Be(enqueued);
        (await h.RunAsync()).CoverageNote.Should().NotBeNullOrEmpty();
    }

    // ── R6: comment commands ──────────────────────────────────────────────────────────────────────

    private static Git.Dependencies.LookupDocument ChalkAndNxMajor()
        => Lookup(Npm("chalk", "4.1.0", "4.1.2", Git.Dependencies.EUpdateType.Patch), Npm("nx", "22.7.12", "23.3.0", Git.Dependencies.EUpdateType.Major));

    [Fact]
    public async Task R6_a_non_writer_approve_gets_thumbs_down_and_changes_nothing()
    {
        var h = await HarnessAsync();
        await PublishAsync(h, ChalkAndNxMajor());

        await h.CommandAsync(EDependencyCommand.Approve, "nx", actor: "visitor");

        h.Updates.Reactions.Should().Equal([(900L, EForgeReaction.ThumbsDown)]);
        (await h.StateAsync()).Approvals.Should().BeEmpty();
    }

    [Fact]
    public async Task R6_a_writer_approve_binds_the_group_and_starts_a_refresh()
    {
        var h = await HarnessAsync();
        await PublishAsync(h, ChalkAndNxMajor());
        var before = (await h.RunAsync()).Run;

        await h.CommandAsync(EDependencyCommand.Approve, "nx");

        var approval = (await h.StateAsync()).Approvals.Should().ContainSingle().Which;
        approval.Group.Should().Be("nx");
        approval.ApprovedBy.Should().Be("maintainer");
        approval.TargetMajors.Should().Equal(["nx@23"]);
        h.Updates.Reactions.Should().Equal([(900L, EForgeReaction.ThumbsUp)]);
        var run = await h.RunAsync();
        run.Run.Should().Be(before + 1);
        run.Trigger.Should().Be(EDependencyRunTrigger.Refresh);
    }

    [Fact]
    public async Task R6_approving_what_does_not_wait_for_approval_is_refused()
    {
        var h = await HarnessAsync();
        await PublishAsync(h, Chalk());

        await h.CommandAsync(EDependencyCommand.Approve, "chalk");

        h.Updates.Reactions.Should().Equal([(900L, EForgeReaction.ThumbsDown)]);
        (await h.StateAsync()).Approvals.Should().BeEmpty();
    }

    [Fact]
    public async Task R6_hold_and_unhold_by_a_writer()
    {
        var h = await HarnessAsync();
        await PublishAsync(h, Chalk());

        await h.CommandAsync(EDependencyCommand.Hold, "chalk", commentId: 901);
        (await h.StateAsync()).Holds.Should().ContainSingle(x => x.Id == "command:chalk" && x.Group == "chalk");

        await h.CommandAsync(EDependencyCommand.Unhold, "chalk", commentId: 902);
        (await h.StateAsync()).Holds.Should().NotContain(x => x.Id == "command:chalk");

        await h.CommandAsync(EDependencyCommand.Unhold, "nothing-held", commentId: 903);
        h.Updates.Reactions.Should().Equal([(901L, EForgeReaction.ThumbsUp), (902L, EForgeReaction.ThumbsUp), (903L, EForgeReaction.ThumbsDown)]);
    }

    [Fact]
    public async Task R6_a_command_on_another_pull_request_is_ignored()
    {
        var h = await HarnessAsync();
        await PublishAsync(h, Chalk());

        await h.SignalAsync(new DependencyUpdateSignalMessage
        {
            RepositoryId = RepositoryId, Kind = EDependencySignal.Command, Number = 1, CommentId = 77, Command = EDependencyCommand.Refresh, Actor = "maintainer",
        });

        h.Updates.Reactions.Should().BeEmpty();
        h.Queue.RoleQueries.Should().NotContain("maintainer");
    }

    // ── R9: migrations ────────────────────────────────────────────────────────────────────────────

    private async Task<DependencyHarness> ApprovedNxAsync()
    {
        var h = await HarnessAsync();
        await h.StoreAsync(new DependencyUpdateState
        {
            Id = DependencyUpdateState.ForRepository(RepositoryId), Repository = RepositoryId,
            Approvals = [new DependencyApproval { Group = "nx", DepNames = ["nx"], TargetMajors = ["nx@23"], ConfigBlob = string.Empty, ApprovedBy = "maintainer" }],
        }, DependencyUpdateState.ForRepository(RepositoryId));
        await h.RunToApplyAsync(ChalkAndNxMajor());
        h.ScriptRun(MigrateRunId, h.Writer.StagedSha, branch: DependencyUpdateRun.WorkBranch);
        return h;
    }

    private static byte[] Marker(string baseSha)
    {
        var node = JsonNode.Parse(DependencyTestKit.ContractFixture("valid", "migrate-result.nx-22-to-23.json"))!;
        node["baseSha"] = baseSha;
        return Encoding.UTF8.GetBytes(node.ToJsonString());
    }

    [Fact]
    public async Task R9_an_approved_major_with_a_migrate_rule_runs_its_migration_on_the_staged_commit()
    {
        var h = await ApprovedNxAsync();
        h.Updates.Artifacts[MigrateRunId] = new() { ["migrate-result.json"] = Marker(h.Writer.StagedSha), ["migrate.patch"] = Encoding.UTF8.GetBytes("diff --git a/nx.json b/nx.json\n") };

        await h.FinishApplyAsync();

        var staged = h.Writer.Requests("work-ref")[^1];
        staged.PlanJson.Should().BeNull();
        staged.Patch.Should().NotBeNull("the migrate run builds expected_sha + the applied patch");
        var migrate = h.Queue.Dispatches[^1];
        migrate.Workflow.Should().Be("nx-migrate.yml");
        migrate.Inputs["base_sha"].Should().Be(h.Writer.StagedSha);

        await h.ReleaseAsync(EDependencyStep.PollRun);

        h.Writer.Requests("rolling").Should().ContainSingle().Which.Migrations.Should().ContainSingle(m => m.Group == "nx");
        var run = await h.RunAsync();
        run.MigratedGroups.Should().Equal(["nx"]);
        h.Updates.Created.Single().Body.Should().Contain("Contains code migrations");
    }

    [Fact]
    public async Task R9_a_migration_without_its_completion_marker_drops_the_group_and_plans_again()
    {
        var h = await ApprovedNxAsync();
        h.Updates.Artifacts[MigrateRunId] = new() { ["migrate.patch"] = Encoding.UTF8.GetBytes("diff --git a/nx.json b/nx.json\n") };

        await h.FinishApplyAsync();
        await h.ReleaseAsync(EDependencyStep.PollRun);

        var replanned = h.Writer.Requests("work-ref").Where(r => r.PlanJson is not null).ToList();
        replanned.Should().HaveCount(2);
        DependencyContract.Deserialize<DependencyPlan>(replanned[^1].PlanJson!)!.Groups.Select(g => g.Group).Should().Equal(["chalk"]);
        (await h.StateAsync()).Holds.Should().ContainSingle(x => x.Id == "migrate:nx");
        (await h.RunAsync()).DroppedGroups.Should().Equal(["nx"]);
    }

    // ── R10: security ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task R10_a_security_run_is_out_of_slot()
    {
        var h = await HarnessAsync();
        h.ScriptLookup(Chalk());
        h.ScriptApply();
        h.Queue.DispatchQueue.Enqueue(new ForgeWorkflowDispatch(EForgeDispatchOutcome.Dispatched, ApplyRunId, null, null));

        await h.TriggerAsync(EDependencyRunTrigger.Security, [new DependencySecurityFixRecord { Ecosystem = "npm", DepName = "chalk", Advisory = "GHSA-xxxx" }]);
        await h.ReleaseAsync(EDependencyStep.PollRun);

        var run = await h.RunAsync();
        run.Trigger.Should().Be(EDependencyRunTrigger.Security);
        run.SecurityFixes.Should().ContainSingle(f => f.DepName == "chalk");
        (await h.StateAsync()).Slots.Should().Be(0, "an out-of-slot run does not advance the four-slot clock (D-P8)");
        DependencyContract.Deserialize<DependencyPlan>(h.Writer.Requests("work-ref")[0].PlanJson!)!.Groups.Select(g => g.Group).Should().Contain("chalk");
    }

    [Fact]
    public async Task R10_a_security_trigger_interrupts_a_bisection()
    {
        var h = await HarnessAsync();
        await h.StoreAsync(new DependencyUpdateRun
        {
            Id = DependencyUpdateRun.ForRepository(RepositoryId), Repository = RepositoryId, Run = 3, State = EDependencyRunState.Bisecting,
        }, DependencyUpdateRun.ForRepository(RepositoryId));

        await h.TriggerAsync(EDependencyRunTrigger.Security, [new DependencySecurityFixRecord { Ecosystem = "npm", DepName = "chalk" }]);

        var run = await h.RunAsync();
        run.Run.Should().Be(4);
        run.State.Should().Be(EDependencyRunState.Starting);
        run.Trigger.Should().Be(EDependencyRunTrigger.Security);
    }

    [Fact]
    public async Task A_scheduled_trigger_during_a_run_waits_for_it()
    {
        var h = await HarnessAsync();
        await h.RunToApplyAsync(Chalk());

        await h.TriggerAsync(EDependencyRunTrigger.Scheduled);

        var run = await h.RunAsync();
        run.State.Should().Be(EDependencyRunState.Applying);
        run.PendingTrigger.Should().Be(EDependencyRunTrigger.Scheduled);
    }

    // ── N6: red CI, bisect ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task N6_red_without_a_bisect_budget_reports_the_suspects()
    {
        var h = await HarnessAsync(bisectBudget: 0);
        await PublishAsync(h, ChalkAndLodash());
        h.Queue.CheckStates[h.Writer.RollingSha] = Red(h.Writer.RollingSha);

        await h.ReleaseAsync(EDependencyStep.PollChecks);

        (await h.RunAsync()).Outcome.Should().Be("red");
        h.Queue.Comments.Should().ContainSingle().Which.Body.Should().Contain("No culprit was found").And.Contain("`lodash`");
    }

    [Fact]
    public async Task N6_red_reruns_once_then_leaves_suspects_out_and_holds_the_culprit()
    {
        var h = await HarnessAsync(bisectBudget: 4);
        await PublishAsync(h, ChalkAndLodash());
        var full = h.Writer.RollingSha;
        h.Queue.CheckStates[full] = Red(full);

        await h.ReleaseAsync(EDependencyStep.PollChecks);

        h.Queue.Reruns.Should().Equal([42L]);
        var bisecting = await h.RunAsync();
        bisecting.State.Should().Be(EDependencyRunState.Bisecting);
        bisecting.Bisect!.Suspects.Should().Equal(["lodash", "chalk"], "the later plan group is more suspect (minor after patch)");
        h.Updates.Checks[^1].Check.State.Should().Be(EForgeQueueCheckState.InProgress);

        // Still red after the re-run: the commit without the top suspect is built and tested.
        h.Time.Now = h.Time.Now.AddMinutes(3);
        h.Writer.RollingSha = "c4c4c4c4c4c4c4c4c4c4c4c4c4c4c4c4c4c4c4c4";
        await h.ReleaseAsync(EDependencyStep.PollChecks);
        var plans = h.Writer.Requests("work-ref").Where(r => r.PlanJson is not null).ToList();
        DependencyContract.Deserialize<DependencyPlan>(plans[^1].PlanJson!)!.Groups.Select(g => g.Group).Should().Equal(["chalk"]);
        await h.WriteApplyArtifactAsync();
        await h.ReleaseAsync(EDependencyStep.PollRun);
        (await h.RunAsync()).State.Should().Be(EDependencyRunState.Bisecting);

        // Green without lodash: lodash is the culprit.
        h.Queue.CheckStates[h.Writer.RollingSha] = Green(h.Writer.RollingSha);
        await h.ReleaseAsync(EDependencyStep.PollChecks);

        (await h.StateAsync()).Holds.Should().ContainSingle(x => x.Id == "red:lodash");
        (await h.FleetAsync())!.RedVersions.Should().ContainSingle(r => r.DepName == "lodash" && r.Version == "4.17.21" && r.Repository == RepositoryId);
        h.Queue.Comments.Should().Contain(c => c.Body.Contains("Bisect: `lodash` broke CI"));
        var after = await h.RunAsync();
        after.Trigger.Should().Be(EDependencyRunTrigger.Refresh, "everything but the culprit is rebuilt");
    }

    // ── R16: onboarding ───────────────────────────────────────────────────────────────────────────

    private const string ActionSha = "0123456789abcdef0123456789abcdef01234567";

    private static DependencyHarness Onboardable(DependencyHarness h)
    {
        h.Queue.Blobs.Remove("master:.github/workflows/dependency-lookup.yml");
        h.SetYml("mergeQueue:\n  enabled: true\n");
        h.Forge.Files["master/.github/dependabot.yml"] = File.ReadAllText(Path.Combine(DependencyTestKit.RepositoryRoot(), ".github", "dependabot.yml"));
        h.Forge.Trees["master"] = new ForgeTree(BaseSha, "tree", [new ForgeTreeEntry("package.json", 10), new ForgeTreeEntry("nx.json", 10)], false);
        h.Updates.Tags[$"{DependencyOnboarding.ActionRepository}@{DependencyOnboarding.ActionTag}"] = ActionSha;
        return h;
    }

    [Fact]
    public async Task R16_onboarding_opens_a_pull_request_with_pinned_templates_and_drops_dependabot_version_updates()
    {
        var h = Onboardable(await HarnessAsync());

        await h.SignalAsync(new DependencyUpdateSignalMessage { RepositoryId = RepositoryId, Kind = EDependencySignal.Onboard });

        var files = h.Writer.Requests("files").Should().ContainSingle().Which.Files!;
        files.Select(f => f.Path).Should().BeEquivalentTo([".github/workflows/dependency-lookup.yml", ".github/workflows/dependency-apply.yml",
            ".github/workflows/nx-migrate.yml", "coverage.yml", ".github/dependabot.yml"]);
        files.Single(f => f.Path.EndsWith("dependency-lookup.yml")).Content.Should().Contain($"dependency-action@{ActionSha} # dependency-update-v1");
        files.Single(f => f.Path == "coverage.yml").Content.Should().Contain("mergeQueue:").And.Contain("# dependencies:");
        files.Single(f => f.Path == ".github/dependabot.yml").Delete.Should().BeTrue("every version-update entry is gone and nothing else remains");
        var pull = h.Updates.Created.Should().ContainSingle().Which;
        pull.Draft.Should().BeFalse();
        pull.Head.Should().Be(DependencyUpdateRun.OnboardingBranch);
        pull.Body.Should().Contain("\"@angular/*\": minor").And.Contain("alerts");
        var run = await h.RunAsync();
        run.Onboarding.Should().Be(EDependencyOnboarding.PullRequestOpened);
        run.OnboardingPullRequestNumber.Should().Be(pull.Number(h));
    }

    [Fact]
    public async Task R16_a_merged_onboarding_pull_request_starts_the_first_run()
    {
        var h = Onboardable(await HarnessAsync());
        await h.SignalAsync(new DependencyUpdateSignalMessage { RepositoryId = RepositoryId, Kind = EDependencySignal.Onboard });

        await h.SignalAsync(new DependencyUpdateSignalMessage { RepositoryId = RepositoryId, Kind = EDependencySignal.Merged, Number = h.Updates.LastPull.Number });

        var run = await h.RunAsync();
        run.Onboarding.Should().Be(EDependencyOnboarding.Done);
        run.State.Should().Be(EDependencyRunState.Starting);
        run.FirstPullRequestOpened.Should().BeFalse("the first rolling pull request is still to come, as a draft");
    }

    [Fact]
    public async Task R16_onboarding_without_the_workflows_permission_is_blocked_with_the_reason()
    {
        var h = Onboardable(await HarnessAsync());
        h.Permissions.Add(new MissingWorkflows());

        await h.SignalAsync(new DependencyUpdateSignalMessage { RepositoryId = RepositoryId, Kind = EDependencySignal.Onboard });

        h.Writer.Requests("files").Should().BeEmpty();
        var run = await h.RunAsync();
        run.Onboarding.Should().Be(EDependencyOnboarding.Blocked);
        run.OnboardingNote.Should().Contain("Workflows");
    }

    private sealed class MissingWorkflows : IForgeInstallationPermissions
    {
        public EForgeProvider Provider => EForgeProvider.GitHub;

        public Task<PendingInstallationPermissions?> GetPendingAsync(Account account, CancellationToken cancellationToken = default)
            => Task.FromResult<PendingInstallationPermissions?>(new PendingInstallationPermissions("https://github.com/settings/installations/4242",
                [new PendingPermission("workflows", "Workflows", "write", null)]));

        public IReadOnlyList<PendingPermission> NeededPending(IReadOnlyList<PendingPermission> pending, EMergeQueueAppNeed needs) => pending;
    }

    // ── Gap C: the bot pins the SHA ───────────────────────────────────────────────────────────────

    private const string OldPin = "1111111111111111111111111111111111111111";
    private const string LookupDigest = "3333333333333333333333333333333333333333";
    private const string BotResolved = "4444444444444444444444444444444444444444";

    private static Git.Dependencies.LookupDocument Checkout() => Lookup(new Git.Dependencies.LookupRecord
    {
        Ecosystem = "github-actions", Manager = "github-actions", Datasource = "github-tags", PackageFile = ".github/workflows/ci.yml",
        DepName = "actions/checkout", PackageName = "actions/checkout", CurrentValue = "v4.1.0", CurrentDigest = OldPin,
        Candidates = [new Git.Dependencies.LookupCandidate
        {
            Version = "v4.2.0", Value = "v4.2.0", UpdateType = Git.Dependencies.EUpdateType.Minor, ReleaseTimestamp = "2026-09-01T00:00:00.000Z", Digest = LookupDigest,
        }],
    });

    [Fact]
    public async Task GapC_a_sha_pinned_uses_bump_is_planned_at_the_commit_the_bot_resolved()
    {
        var h = await HarnessAsync();
        h.Updates.Tags["actions/checkout@v4.2.0"] = BotResolved;
        h.Updates.Files[$"actions/checkout@{BotResolved}:action.yml"] = "name: checkout\nruns:\n  using: node24\n  main: index.js\n";

        await h.RunToApplyAsync(Checkout());
        await h.FinishApplyAsync();

        var plan = DependencyContract.Deserialize<DependencyPlan>(h.Writer.Requests("work-ref")[0].PlanJson!)!;
        plan.Groups.Single().Items.Single().NewDigest.Should().Be(BotResolved, "the lookup's digest is not trusted for the pin (§13.11.3 #10)");
        h.Writer.Requests("validate").Single().TargetActions.Should().ContainKey($"actions/checkout@{BotResolved}");
    }

    [Fact]
    public async Task GapC_a_tag_the_bot_cannot_resolve_is_not_planned()
    {
        var h = await HarnessAsync();

        await h.RunToApplyAsync(Checkout());

        h.Writer.Requests("work-ref").Should().BeEmpty();
        (await h.RunAsync()).Outcome.Should().Be("nothing-planned");
    }

    /// <summary>
    /// D-O20 (live test 2026-10-17): a run that opens no pull request says why. With every remaining update above the
    /// default <c>minor</c> threshold, "Nothing to apply this time." read as silence; it counts what waits for approval.
    /// </summary>
    [Fact]
    public async Task D_O20_nothing_within_the_thresholds_says_how_many_groups_await_approval()
    {
        var h = await HarnessAsync();

        await h.RunToApplyAsync(Lookup(Npm("lodash", "3.10.1", "4.17.21", Git.Dependencies.EUpdateType.Major)));

        h.Writer.Requests("work-ref").Should().BeEmpty();
        var run = await h.RunAsync();
        run.Outcome.Should().Be("nothing-planned");
        run.OutcomeMessage.Should().Be("Nothing within your thresholds to apply this time; 1 group awaiting approval.");
    }
}

file static class PullExtensions
{
    public static int Number(this (string Head, string Base, string Title, string Body, bool Draft) created, DependencyHarness h)
        => h.Updates.PullRequests.Values.Single(p => p.HeadRef == created.Head).Number;
}
