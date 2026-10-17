using CodeCoverage.Dependencies.Lifecycle;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.LookupReferences;
using Xunit;
using static CodeCoverage.Tests.Dependencies.Lifecycle.DependencyHarness;

namespace CodeCoverage.Tests.Dependencies.Lifecycle;

/// <summary>
/// The event-driven onboarding and offboarding (dependency-updates PRD §13.13: P15, R24, R25, R26) on the saga's harness:
/// the forge and the git writer scripted, the run state in a real RavenDB.
/// </summary>
public class DependencyOnboardingTests : CoverageRavenTest
{
    private const string ActionSha = "0123456789abcdef0123456789abcdef01234567";

    /// <summary>What the fake writer answers for the onboarding commit (<c>FakeGitWriter.DependencyFilesAsync</c>).</summary>
    private const string OnboardingCommit = "e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5";

    private async Task<DependencyHarness> OnboardableAsync(string yml = "mergeQueue:\n  enabled: true\n")
    {
        var h = new DependencyHarness(GetDocumentStore());
        await h.SeedAsync();
        h.Queue.Blobs.Remove("master:.github/workflows/dependency-lookup.yml");
        h.SetYml(yml);
        h.Forge.Trees["master"] = new ForgeTree(BaseSha, "tree", [new ForgeTreeEntry("package.json", 10)], false);
        h.Updates.Tags[$"{DependencyOnboarding.ActionRepository}@{DependencyOnboarding.ActionTag}"] = ActionSha;
        // The pull request GitHub opens from the onboarding branch reports the branch's head: the commit the writer pushed.
        h.Updates.HeadShaFor = branch => branch == DependencyUpdateRun.OnboardingBranch ? OnboardingCommit : null;
        return h;
    }

    private static DependencyUpdateSignalMessage Onboard => new() { RepositoryId = RepositoryId, Kind = EDependencySignal.Onboard };

    /// <summary>What the bus does with a failed handler: deliver the same message again.</summary>
    private static async Task DeliverUntilDoneAsync(DependencyHarness h, Action? betweenAttempts = null)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                await h.SignalAsync(Onboard);
                return;
            }
            catch (Exception ex) when (ex is ForgeRequestException or HttpRequestException)
            {
                betweenAttempts?.Invoke();
            }
        }
        throw new InvalidOperationException("The onboarding never completed.");
    }

    private static ForgeRequestException RateLimited() => new("GitHub rate limit reached.", 429,
        new ForgeRateLimitedException(TimeSpan.FromMinutes(1), null, EForgeRateLimitKind.Secondary, "secondary rate limit"));

    // ── P15: idempotent, whatever step fails ─────────────────────────────────────────────────────

    [Fact]
    public async Task P15_a_failure_before_the_push_retries_into_one_branch_and_one_pull_request()
    {
        var h = await OnboardableAsync();
        h.Updates.Faults.Add(("find", 1, RateLimited()));

        await DeliverUntilDoneAsync(h);

        h.Writer.Requests("files").Should().ContainSingle();
        h.Updates.Created.Should().ContainSingle();
        (await h.RunAsync()).Onboarding.Should().Be(EDependencyOnboarding.PullRequestOpened);
    }

    [Fact]
    public async Task P15_a_failure_after_the_push_does_not_push_again()
    {
        var h = await OnboardableAsync();
        // The push lands (the fake writer does not move refs: the test does, as GitHub would), then the pull-request lookup fails.
        h.Updates.Faults.Add(("find", 2, RateLimited()));

        await DeliverUntilDoneAsync(h, () => h.Queue.BranchHeads[DependencyUpdateRun.OnboardingBranch] = OnboardingCommit);

        h.Writer.Requests("files").Should().ContainSingle("the retry found the branch at the commit the first attempt pushed and saved");
        h.Updates.Created.Should().ContainSingle();
        var run = await h.RunAsync();
        run.OnboardingBranchSha.Should().Be(OnboardingCommit);
        run.OnboardingPullRequestNumber.Should().Be(h.Updates.LastPull.Number);
    }

    [Fact]
    public async Task P15_a_pull_request_whose_answer_was_lost_is_found_not_opened_twice()
    {
        var h = await OnboardableAsync();
        // GitHub opened it, but the answer never came back.
        h.Updates.Faults.Add(("create!", 1, new HttpRequestException("connection reset")));

        await DeliverUntilDoneAsync(h, () => h.Queue.BranchHeads[DependencyUpdateRun.OnboardingBranch] = OnboardingCommit);

        h.Updates.Created.Should().ContainSingle();
        h.Writer.Requests("files").Should().ContainSingle();
        var run = await h.RunAsync();
        run.Onboarding.Should().Be(EDependencyOnboarding.PullRequestOpened);
        run.OnboardingPullRequestNumber.Should().Be(h.Updates.LastPull.Number);
    }

    [Fact]
    public async Task P15_a_retry_rebuilds_the_same_commit()
    {
        var h = await OnboardableAsync();
        h.Updates.Faults.Add(("find", 2, RateLimited()));

        // No ref moves between the attempts this time: the branch is not at the saved commit, so the retry pushes again, and
        // must push the identical commit (same base, files and date) so the force-push is a no-op.
        await DeliverUntilDoneAsync(h, () => h.Time.Now += TimeSpan.FromMinutes(7));

        var pushes = h.Writer.Requests("files");
        pushes.Should().HaveCount(2);
        pushes[1].ExpectedSha.Should().Be(pushes[0].ExpectedSha);
        pushes[1].Author.Should().Be(pushes[0].Author);
        pushes[1].Committer.Should().Be(pushes[0].Committer);
        pushes[1].Files!.Select(f => (f.Path, f.Content)).Should().Equal(pushes[0].Files!.Select(f => (f.Path, f.Content)));
        h.Updates.Created.Should().ContainSingle();
    }

    [Fact]
    public async Task P15_an_onboarding_the_owner_closed_is_not_opened_again()
    {
        var h = await OnboardableAsync();
        await h.StoreAsync(new DependencyUpdateRun
        {
            Id = DependencyUpdateRun.ForRepository(RepositoryId), Repository = RepositoryId,
            Onboarding = EDependencyOnboarding.Blocked, OnboardingPullRequestNumber = 12, OnboardingNote = "The onboarding pull request was closed without merging.",
        }, DependencyUpdateRun.ForRepository(RepositoryId));

        await h.SignalAsync(Onboard);

        h.Writer.Requests("files").Should().BeEmpty();
        h.Updates.Created.Should().BeEmpty();
        (await h.RunAsync()).Onboarding.Should().Be(EDependencyOnboarding.Blocked);
    }

    // ── D-O15: workflows already on the default branch ───────────────────────────────────────────

    /// <summary>
    /// Live test 2026-10-17: a re-enabled repository whose workflows were already merged became Done without a pull
    /// request and then waited for its schedule (Monday 06:00). It starts the first run at once, as a merged onboarding
    /// pull request does, expedited when the switch was a person's.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task D_O15_workflows_already_on_the_default_branch_start_the_first_run_now(bool expedite)
    {
        var h = new DependencyHarness(GetDocumentStore());
        await h.SeedAsync();

        await h.SignalAsync(Onboard with { Expedite = expedite });

        var run = await h.RunAsync();
        run.Onboarding.Should().Be(EDependencyOnboarding.Done);
        run.State.Should().Be(EDependencyRunState.Starting);
        run.Trigger.Should().Be(EDependencyRunTrigger.Refresh);
        run.FileMergeQueue.Should().Be("enabled", "D-O16: the onboarding check read coverage.yml, so the repository page can say so");
        var start = h.Outbox.Messages.Where(m => m.Message is DependencyUpdateStepMessage { Step: EDependencyStep.Start }).Should().ContainSingle().Which;
        (start.Options?.Expedite ?? false).Should().Be(expedite);
        h.Updates.Created.Should().BeEmpty("no onboarding pull request when the workflows are there");
        (await h.Store.Maintenance.SendAsync(new Raven.Client.Documents.Operations.Indexes.GetIndexNamesOperation(0, 1024)))
            .Where(name => name.StartsWith("Auto/", StringComparison.OrdinalIgnoreCase)).Should().BeEmpty("D-O18: no auto index in the onboarding path");
    }

    // ── R25/R26: the merge queue at onboarding ───────────────────────────────────────────────────

    [Fact]
    public async Task R25_onboarding_adds_the_merge_queue_when_the_file_has_none()
    {
        var h = await OnboardableAsync(yml: "gate:\n  patchTarget: 80\n");

        await h.SignalAsync(Onboard);

        var yml = h.Writer.Requests("files").Single().Files!.Single(f => f.Path == "coverage.yml").Content!;
        yml.Should().StartWith("gate:\n  patchTarget: 80\n").And.Contain("mergeQueue:\n  enabled: true\n").And.Contain("# dependencies:");
        h.Updates.Created.Single().Body.Should().Contain("mergeQueue: { enabled: true }");
        (await h.RunAsync()).FileMergeQueue.Should().Be("absent", "what the file said when onboarding read it");
    }

    [Fact]
    public async Task R26_a_repository_set_to_no_leaves_the_file_without_the_merge_queue()
    {
        var h = await OnboardableAsync(yml: "gate:\n  patchTarget: 80\n");
        using (var session = h.Store.OpenAsyncSession())
        {
            (await session.LoadAsync<Repository>(RepositoryId)).DependencyOnboardingMergeQueue = EDependencyOnboardingMergeQueuePolicy.No;
            await session.SaveChangesAsync();
        }

        await h.SignalAsync(Onboard);

        h.Writer.Requests("files").Single().Files!.Single(f => f.Path == "coverage.yml").Content.Should().NotContain("mergeQueue:");
    }

    [Fact]
    public async Task R25_a_merge_queue_section_already_there_is_the_owners()
    {
        var h = await OnboardableAsync(yml: "mergeQueue:\n  enabled: false\n");

        await h.SignalAsync(Onboard);

        var yml = h.Writer.Requests("files").Single().Files!.Single(f => f.Path == "coverage.yml").Content!;
        yml.Should().StartWith("mergeQueue:\n  enabled: false\n");
        yml.Should().NotContain("mergeQueue:\n  enabled: true", "nothing is added beside the owner's section");
        (await h.RunAsync()).FileMergeQueue.Should().Be("disabled");
    }

    [Theory]
    [InlineData("mergeQueue:\n  enabled: true\n", true)]
    [InlineData("gate: {}\nmergeQueue: { enabled: false }\n", true)]
    [InlineData("# mergeQueue:\n#   enabled: true\n", false)]
    [InlineData("gate:\n  mergeQueue: x\n", false)]
    [InlineData(null, false)]
    public void Only_a_top_level_key_is_a_section(string? yml, bool expected)
        => DependencyOnboarding.HasTopLevelKey(yml, "mergeQueue").Should().Be(expected);

    // ── R24: offboarding ─────────────────────────────────────────────────────────────────────────

    private async Task<DependencyHarness> RunningAsync()
    {
        var h = new DependencyHarness(GetDocumentStore());
        await h.SeedAsync();
        h.Updates.PullRequests[41] = new ForgeBotPullRequest(41, DependencyUpdateRun.RollingBranch, "c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3", "master", true, false, false, null);
        await h.StoreAsync(new DependencyUpdateRun
        {
            Id = DependencyUpdateRun.ForRepository(RepositoryId), Repository = RepositoryId, Onboarding = EDependencyOnboarding.Done, FirstPullRequestOpened = true,
            State = EDependencyRunState.AwaitingChecks, Run = 3, Generation = 7, PullRequestNumber = 41,
            RollingSha = "c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3", RollingBotSha = "c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3", WorkRefSha = "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1",
        }, DependencyUpdateRun.ForRepository(RepositoryId));
        var state = new DependencyUpdateState { Id = DependencyUpdateState.ForRepository(RepositoryId) };
        state.Holds.Add(new DependencyHold { Id = "human:lodash", Kind = EDependencyHoldKind.Human, DepNames = ["lodash"], Reason = "set by hand" });
        await h.StoreAsync(state, state.Id);
        return h;
    }

    private static async Task SwitchOffAsync(DependencyHarness h)
    {
        using var session = h.Store.OpenAsyncSession();
        (await session.LoadAsync<Repository>(RepositoryId)).DependencyUpdates = EDependencyUpdatesPolicy.Disabled;
        await session.SaveChangesAsync();
    }

    [Fact]
    public async Task R24_offboarding_closes_the_rolling_pull_request_deletes_the_branches_and_keeps_the_holds()
    {
        var h = await RunningAsync();
        await SwitchOffAsync(h);

        await h.SignalAsync(new DependencyUpdateSignalMessage { RepositoryId = RepositoryId, Kind = EDependencySignal.Offboard });

        h.Queue.Comments.Should().ContainSingle(c => c.Number == 41).Which.Body.Should().Contain("switched off");
        h.Updates.Closed.Should().Equal([41]);
        h.Forge.DeletedBranches.Should().BeEquivalentTo([DependencyUpdateRun.RollingBranch, DependencyUpdateRun.WorkBranch, DependencyUpdateRun.OnboardingBranch]);
        var run = await h.RunAsync();
        run.PullRequestNumber.HasValue.Should().BeFalse();
        run.RollingSha.Should().BeNull();
        run.WorkRefSha.Should().BeNull();
        run.State.Should().Be(EDependencyRunState.Idle);
        run.Onboarding.Should().Be(EDependencyOnboarding.Done, "the templates stay: they are the owner's code");
        (await h.StateAsync()).Holds.Should().ContainSingle(x => x.Id == "human:lodash", "holds and approvals are kept");
    }

    [Fact]
    public async Task R24_an_offboard_that_arrives_after_switching_on_again_does_nothing()
    {
        var h = await RunningAsync();

        await h.SignalAsync(new DependencyUpdateSignalMessage { RepositoryId = RepositoryId, Kind = EDependencySignal.Offboard });

        h.Updates.Closed.Should().BeEmpty();
        h.Forge.DeletedBranches.Should().BeEmpty();
        (await h.RunAsync()).PullRequestNumber.Should().Be(41);
    }

    [Fact]
    public async Task R24_offboarding_lets_a_later_switch_on_onboard_again_after_an_owner_closed_the_pull_request()
    {
        var h = await OnboardableAsync();
        await h.StoreAsync(new DependencyUpdateRun
        {
            Id = DependencyUpdateRun.ForRepository(RepositoryId), Repository = RepositoryId,
            Onboarding = EDependencyOnboarding.Blocked, OnboardingPullRequestNumber = 12,
        }, DependencyUpdateRun.ForRepository(RepositoryId));
        await SwitchOffAsync(h);
        await h.SignalAsync(new DependencyUpdateSignalMessage { RepositoryId = RepositoryId, Kind = EDependencySignal.Offboard });
        using (var session = h.Store.OpenAsyncSession())
        {
            (await session.LoadAsync<Repository>(RepositoryId)).DependencyUpdates = EDependencyUpdatesPolicy.Enabled;
            await session.SaveChangesAsync();
        }

        await h.SignalAsync(Onboard);

        h.Updates.Created.Should().ContainSingle("an explicit switch on is the owner asking again");
    }
}
