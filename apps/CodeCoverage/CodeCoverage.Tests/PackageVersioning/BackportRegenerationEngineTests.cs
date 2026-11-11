using CodeCoverage.CommitGraph;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Git.MergeQueue;
using CodeCoverage.MergeQueue;
using CodeCoverage.PackageVersioning;
using CodeCoverage.WorkflowHooks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using NSubstitute;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.PackageVersioning;

/// <summary>
/// #570 M6 (PRD D9): a backport waits for its line's regenerate workflow, the patch is folded into the backport commit,
/// and the pull request opens; whatever stops the regeneration opens it with a note instead.
/// </summary>
public class BackportRegenerationEngineTests : CoverageRavenTest
{
    private const string RepositoryId = "Repositories/github/153";
    private const string Head = "backport/7-8.x";
    private static readonly string Backport = new('d', 40);
    private static readonly string Folded = new('f', 40);

    private readonly FakeForgeVersioning forge = new();
    private readonly IForgeMergeQueue queue = Substitute.For<IForgeMergeQueue>();
    private readonly IGitWriterSidecar writer = Substitute.For<IGitWriterSidecar>();
    private readonly IWorkflowHook hook = Substitute.For<IWorkflowHook>();
    private readonly Outbox outbox = new();
    private readonly List<WorkflowHookRequest> dispatches = [];

    public BackportRegenerationEngineTests()
    {
        queue.Provider.Returns(EForgeProvider.GitHub);
        writer.IsConfigured.Returns(true);
        hook.VerifyProvenanceAsync(Arg.Any<IWorkflowHookSaga>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns((WorkflowHookProvenanceFailure?)null);
        hook.DispatchAsync(Arg.Any<IWorkflowHookSaga>(), Arg.Any<WorkflowHookRequest>(), Arg.Any<Func<WorkflowHookDispatch, Task>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var request = call.ArgAt<WorkflowHookRequest>(1);
                dispatches.Add(request);
                var dispatched = new WorkflowHookDispatch(EWorkflowHookDispatch.Dispatched, request.Workflow, 40 + dispatches.Count, "https://example.invalid/run", EForgeDispatchOutcome.Dispatched, null);
                await call.ArgAt<Func<WorkflowHookDispatch, Task>>(2)(dispatched);
                return dispatched;
            });
        hook.ObserveAsync(Arg.Any<IWorkflowHookSaga>(), Arg.Any<WorkflowHookRun>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var run = call.ArgAt<WorkflowHookRun>(1);
                run.Finished = run.Succeeded = true;
                return new WorkflowHookObservation(EWorkflowHookRunVerdict.Succeeded, new ForgeWorkflowRun(run.RunId!.Value, "completed", "success", call.ArgAt<string>(2), Head, null));
            });
        hook.DownloadArtifactAsync(Arg.Any<IWorkflowHookSaga>(), Arg.Any<long>(), "merge-queue-patch", Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new ForgeArtifactDownload(EForgeArtifactOutcome.Ok, "patch.diff", [1, 2, 3], null));
        hook.RunWriterJobAsync(Arg.Any<IWorkflowHookSaga>(), null, false, Arg.Any<Func<ForgeWriterAccess, SidecarCredential, Task<GitWriterBackportRegeneratedResult>>>(), Arg.Any<CancellationToken>())
            .Returns(async call => new WorkflowHookWriterJob<GitWriterBackportRegeneratedResult>(true,
                await call.ArgAt<Func<ForgeWriterAccess, SidecarCredential, Task<GitWriterBackportRegeneratedResult>>>(3)(
                    new ForgeWriterAccess("https://example.invalid/acme/widgets.git", "https://example.invalid/acme/widgets.git", "x", "y"), new SidecarCredential("x", "y")), null));
        writer.VersioningBackportRegeneratedAsync(Arg.Any<GitWriterBackportRegeneratedRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GitWriterBackportRegeneratedResult(GitWriterBackportRegeneratedResult.Regenerated, null, Folded, ["app/package-lock.json"], null, 1));
    }

    private BackportRegenerationEngine Engine(IAsyncDocumentSession session)
        => new(session, [forge], [queue], writer, outbox, hook, TimeProvider.System, Options.Create(new MergeQueueOptions()), NullLogger<BackportRegenerationEngine>.Instance);

    private static BackportRegeneration Item(params string[] workflows) => new()
    {
        Head = Head, Branch = "release/8.x", Number = 7, Title = "[8.x] fix: guard", Body = "Backport of #7 to `release/8.x`.", Sha = Backport,
        Workflows = [.. workflows], RegeneratedPaths = ["**/package-lock.json"], Paths = ["app/package-lock.json"],
    };

    private async Task BeginAsync(IDocumentStore store, BackportRegeneration item)
    {
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new Repository { FullName = "acme/widgets", Provider = EForgeProvider.GitHub, GitHubId = 153, DefaultBranch = "main" }, RepositoryId);
            await seed.SaveChangesAsync();
        }
        using var session = store.OpenAsyncSession();
        var repository = await session.LoadAsync<Repository>(RepositoryId);
        await Engine(session).BeginAsync(repository, item);
    }

    /// <summary>Handles the step last queued, or <paramref name="step"/>.</summary>
    private async Task StepAsync(IDocumentStore store, BackportRegenerationStepMessage? step = null)
    {
        using var session = store.OpenAsyncSession();
        await Engine(session).HandleStepAsync(step ?? outbox.Steps.Last());
    }

    private async Task<BackportRegeneration?> PendingAsync(IDocumentStore store)
    {
        using var session = store.OpenAsyncSession();
        return (await session.LoadAsync<BackportRegenerations>(BackportRegenerations.ForRepository(RepositoryId)))?.Pending.SingleOrDefault();
    }

    private BackportRegenerationStepMessage Completed(BackportRegeneration item)
        => new() { RepositoryId = RepositoryId, Head = Head, Generation = item.Generation, Step = EBackportRegenerationStep.RunCompleted, RunId = item.Runs.Last().RunId };

    [Fact]
    public async Task The_patch_is_folded_and_the_pull_request_opens_regenerated()
    {
        using var store = GetDocumentStore();
        await BeginAsync(store, Item("regenerate.yml"));

        await StepAsync(store); // dispatch
        await hook.Received(1).VerifyProvenanceAsync(Arg.Any<IWorkflowHookSaga>(), "release/8.x", Backport, Arg.Is<IReadOnlyList<string>>(w => w.Single() == "regenerate.yml"), Arg.Any<CancellationToken>());
        var request = dispatches.Single();
        (request.Workflow, request.Ref, request.Inputs["expected_sha"], request.Inputs["pr_number"]).Should().Be(("regenerate.yml", Head, Backport, "7"));
        forge.OpenedPullRequests.Should().BeEmpty();

        await StepAsync(store, Completed((await PendingAsync(store))!));
        await writer.Received(1).VersioningBackportRegeneratedAsync(Arg.Is<GitWriterBackportRegeneratedRequest>(r =>
            r.Head == Head && r.ExpectedSha == Backport && r.Patch == Convert.ToBase64String(new byte[] { 1, 2, 3 }) && r.RegeneratedPaths.Single() == "**/package-lock.json"),
            Arg.Any<CancellationToken>());
        (await PendingAsync(store))!.Sha.Should().Be(Folded);

        await StepAsync(store); // every workflow ran: open
        var opened = forge.OpenedPullRequests.Single();
        (opened.Head, opened.Base, opened.Title).Should().Be((Head, "release/8.x", "[8.x] fix: guard"));
        opened.Body.Should().Be("Backport of #7 to `release/8.x`.\n\nRegenerated on this line by `regenerate.yml`, folded into the backport commit:\n- `app/package-lock.json`\n");
        forge.Comments.Single().Should().Be((7, "Backport to `release/8.x`: #1000, regenerated by `regenerate.yml`."));
        (await PendingAsync(store)).Should().BeNull();
        (await AttemptAsync(store)).Should().BeEquivalentTo(new { Source = 7, Branch = "release/8.x", Outcome = BackportAttempt.Opened, PullRequest = (int?)1000 });
    }

    /// <summary>Segment B: the one backport record, which the saga moves from "regenerating" to its pull request.</summary>
    private static async Task<BackportAttempt> AttemptAsync(IDocumentStore store)
    {
        using var session = store.OpenAsyncSession();
        return (await session.Advanced.LoadStartingWithAsync<BackportAttempt>(BackportAttempt.Prefix)).Single();
    }

    [Fact]
    public async Task Two_workflows_run_one_after_the_other_on_the_commit_the_first_left()
    {
        using var store = GetDocumentStore();
        await BeginAsync(store, Item("lock.yml", "regenerate.yml"));

        await StepAsync(store);
        await StepAsync(store, Completed((await PendingAsync(store))!));
        await StepAsync(store);

        dispatches.Select(d => (d.Workflow, d.Inputs["expected_sha"])).Should().Equal(("lock.yml", Backport), ("regenerate.yml", Folded));
        // D12 is checked once, for every workflow, before the first dispatch.
        await hook.Received(1).VerifyProvenanceAsync(Arg.Any<IWorkflowHookSaga>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        forge.OpenedPullRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_workflow_that_is_not_the_line_tips_opens_the_pull_request_unregenerated()
    {
        // Owner, 2026-11-05: provenance is the line tip's copy.
        using var store = GetDocumentStore();
        hook.VerifyProvenanceAsync(Arg.Any<IWorkflowHookSaga>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new WorkflowHookProvenanceFailure(EWorkflowHookProvenance.Differs, ".github/workflows/regenerate.yml"));
        await BeginAsync(store, Item("regenerate.yml"));

        await StepAsync(store);

        dispatches.Should().BeEmpty();
        forge.OpenedPullRequests.Single().Body.Should().EndWith(
            "**Not regenerated**: the backport changes `.github/workflows/regenerate.yml`, and only `release/8.x`'s own copy is run. "
            + "These files kept the line's version and need regenerating on this branch:\n- `app/package-lock.json`\n");
        forge.Comments.Single().Body.Should().StartWith("Backport to `release/8.x`: #1000, **not regenerated**: the backport changes");
        (await PendingAsync(store)).Should().BeNull();
        (await AttemptAsync(store)).Outcome.Should().Be(BackportAttempt.NotRegenerated);
    }

    [Fact]
    public async Task A_refused_patch_opens_the_pull_request_with_the_writers_reason()
    {
        using var store = GetDocumentStore();
        writer.VersioningBackportRegeneratedAsync(Arg.Any<GitWriterBackportRegeneratedRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GitWriterBackportRegeneratedResult("patch-rejected", "outside-regenerated-paths", null, ["src/Guard.cs"], null, 1));
        await BeginAsync(store, Item("regenerate.yml"));

        await StepAsync(store);
        await StepAsync(store, Completed((await PendingAsync(store))!));

        forge.OpenedPullRequests.Single().Body.Should().Contain(
            "**Not regenerated**: the git writer did not fold the `regenerate.yml` patch (outside-regenerated-paths on `src/Guard.cs`).");
    }

    [Fact]
    public async Task A_timeout_abandons_the_run_and_a_stale_step_is_dropped()
    {
        using var store = GetDocumentStore();
        await BeginAsync(store, Item("regenerate.yml"));
        await StepAsync(store);
        var item = (await PendingAsync(store))!;

        // A step of the generation before the dispatch: the state has moved on.
        await StepAsync(store, new BackportRegenerationStepMessage { RepositoryId = RepositoryId, Head = Head, Generation = item.Generation - 1, Step = EBackportRegenerationStep.Timeout });
        forge.OpenedPullRequests.Should().BeEmpty();

        await StepAsync(store, new BackportRegenerationStepMessage { RepositoryId = RepositoryId, Head = Head, Generation = item.Generation, Step = EBackportRegenerationStep.Timeout });

        await hook.Received(1).AbandonAsync(Arg.Any<IWorkflowHookSaga>(), Arg.Is<IEnumerable<WorkflowHookRun>>(r => r.Single().RunId == 41), Arg.Any<CancellationToken>());
        forge.OpenedPullRequests.Single().Body.Should().Contain("**Not regenerated**: the regenerate run took longer than 20 minutes.");
        (await PendingAsync(store)).Should().BeNull();
    }

    [Fact]
    public void The_plan_takes_each_resolutions_rule_action_and_adds_concrete_paths_outside_the_list()
    {
        var section = new MergeQueueSection
        {
            PostRebaseActionName = "regenerate.yml",
            RegeneratedPaths = ["**/package-lock.json"],
            AutoResolve = [new MergeQueueAutoResolveRule { Path = "**/packages.lock.json", Action = "lock.yml" }],
        };
        var target = new GitWriterBackportTargetResult("release/8.x", Head, Backport, GitWriterBackportTargetResult.Created, [],
            Resolutions: [new("App/packages.lock.json", "take-base", true, 0)], Regenerate: ["App/packages.lock.json", "app/package-lock.json"]);

        var plan = BackportRegenerationEngine.Plan(section, target);

        plan.Refusal.Should().BeNull();
        plan.Workflows.Should().Equal("lock.yml", "regenerate.yml");
        plan.Globs.Should().Equal("**/package-lock.json", "App/packages.lock.json");
    }

    [Fact]
    public async Task A_regenerated_backport_into_a_queue_line_is_handed_to_the_merge_queue()
    {
        // #570 M7, D12: backport pull requests go through the merge queue, automatically.
        using var store = GetDocumentStore();
        var item = Item("regenerate.yml");
        item.Enqueue = true;
        await BeginAsync(store, item);

        await StepAsync(store);
        await StepAsync(store, Completed((await PendingAsync(store))!));
        await StepAsync(store);

        var signal = outbox.Signals.Single();
        (signal.RepositoryId, signal.Kind, signal.Number, signal.Command, signal.Source, signal.Actor)
            .Should().Be((RepositoryId, EMergeQueueSignal.Command, 1000, EMergeQueueCommand.Enqueue, EMergeQueueCommandSource.Backports, BackportRegenerationEngine.QueueActor));
        forge.Comments.Single().Body.Should().Be("Backport to `release/8.x`: #1000, regenerated by `regenerate.yml`, handed to the merge queue.");
    }

    [Fact]
    public async Task A_backport_that_was_not_regenerated_is_never_enqueued()
    {
        // Its files are stale: a person regenerates them and queues it.
        using var store = GetDocumentStore();
        hook.VerifyProvenanceAsync(Arg.Any<IWorkflowHookSaga>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new WorkflowHookProvenanceFailure(EWorkflowHookProvenance.Missing, ".github/workflows/regenerate.yml"));
        var item = Item("regenerate.yml");
        item.Enqueue = true;
        await BeginAsync(store, item);

        await StepAsync(store);

        outbox.Signals.Should().BeEmpty();
        forge.OpenedPullRequests.Should().ContainSingle();
    }

    /// <summary>Everything that stops a regeneration still opens the pull request, with the reason in its note.</summary>
    public static TheoryData<string, string> Stops => new()
    {
        { "writer-unconfigured", "this server's git writer is not configured" },
        { "dispatch-refused", "the forge refused to run `regenerate.yml` on `backport/7-8.x` (Refused: no workflow_dispatch trigger)" },
        { "dispatch-uncorrelated", "the forge started `regenerate.yml` without saying which run it is, so its result cannot be trusted" },
        { "run-missing", "the `regenerate.yml` run 41 no longer exists" },
        { "run-wrong-commit", "the `regenerate.yml` run built `eeeeeeeeeeee`, not the backport's `dddddddddddd`" },
        { "run-failed", "the `regenerate.yml` run finished `failure` (https://example.invalid/run/41)" },
        { "run-failed-no-conclusion", "the `regenerate.yml` run finished `without a conclusion`" },
        { "artifact-expired", "the `regenerate.yml` run left an unusable `merge-queue-patch` artifact (Expired: gone after 1 day)" },
        { "no-token", "the forge gave no token to push the regenerated files (App not installed)" },
        { "no-token-no-reason", "the forge gave no token to push the regenerated files" },
        { "writer-moved", "the git writer did not fold the `regenerate.yml` patch (branch-moved): the head is now aaaa" },
        { "access-denied", "the forge refused a call the regeneration needs (it asks for `actions: write`)" },
        { "access-denied-bare", "the forge refused a call the regeneration needs" },
        { "writer-refused", "the git writer refused the backport/regenerated request (`patch` must be base64)" },
    };

    [Theory]
    [MemberData(nameof(Stops))]
    public async Task Whatever_stops_the_regeneration_opens_the_pull_request_with_why(string stop, string why)
    {
        using var store = GetDocumentStore();
        ForgeWorkflowRun Run(string sha, string? conclusion, string? url) => new(41, "completed", conclusion, sha, Head, url);
        WorkflowHookDispatch Dispatch(EWorkflowHookDispatch outcome) => new(outcome, "regenerate.yml", null, null, EForgeDispatchOutcome.Refused, "no workflow_dispatch trigger");
        void Observe(EWorkflowHookRunVerdict verdict, ForgeWorkflowRun? run)
            => hook.ObserveAsync(Arg.Any<IWorkflowHookSaga>(), Arg.Any<WorkflowHookRun>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(new WorkflowHookObservation(verdict, run));
        void NoToken(string? refusal)
            => hook.RunWriterJobAsync(Arg.Any<IWorkflowHookSaga>(), null, false, Arg.Any<Func<ForgeWriterAccess, SidecarCredential, Task<GitWriterBackportRegeneratedResult>>>(), Arg.Any<CancellationToken>())
                .Returns(new WorkflowHookWriterJob<GitWriterBackportRegeneratedResult>(false, null, refusal));
        void DispatchThrows(Exception ex)
            => hook.DispatchAsync(Arg.Any<IWorkflowHookSaga>(), Arg.Any<WorkflowHookRequest>(), Arg.Any<Func<WorkflowHookDispatch, Task>>(), Arg.Any<CancellationToken>())
                .Returns<WorkflowHookDispatch>(_ => throw ex);
        switch (stop)
        {
            case "writer-unconfigured": writer.IsConfigured.Returns(false); break;
            case "dispatch-refused":
                hook.DispatchAsync(Arg.Any<IWorkflowHookSaga>(), Arg.Any<WorkflowHookRequest>(), Arg.Any<Func<WorkflowHookDispatch, Task>>(), Arg.Any<CancellationToken>())
                    .Returns(Dispatch(EWorkflowHookDispatch.Refused));
                break;
            case "dispatch-uncorrelated":
                hook.DispatchAsync(Arg.Any<IWorkflowHookSaga>(), Arg.Any<WorkflowHookRequest>(), Arg.Any<Func<WorkflowHookDispatch, Task>>(), Arg.Any<CancellationToken>())
                    .Returns(Dispatch(EWorkflowHookDispatch.Uncorrelated));
                break;
            case "run-missing": Observe(EWorkflowHookRunVerdict.Missing, null); break;
            case "run-wrong-commit": Observe(EWorkflowHookRunVerdict.WrongCommit, Run(new string('e', 40), "success", null)); break;
            case "run-failed": Observe(EWorkflowHookRunVerdict.Failed, Run(Backport, "failure", "https://example.invalid/run/41")); break;
            case "run-failed-no-conclusion": Observe(EWorkflowHookRunVerdict.Failed, Run(Backport, null, null)); break;
            case "artifact-expired":
                hook.DownloadArtifactAsync(Arg.Any<IWorkflowHookSaga>(), Arg.Any<long>(), "merge-queue-patch", Arg.Any<long>(), Arg.Any<CancellationToken>())
                    .Returns(new ForgeArtifactDownload(EForgeArtifactOutcome.Expired, null, null, "gone after 1 day"));
                break;
            case "no-token": NoToken("App not installed"); break;
            case "no-token-no-reason": NoToken(null); break;
            case "writer-moved":
                writer.VersioningBackportRegeneratedAsync(Arg.Any<GitWriterBackportRegeneratedRequest>(), Arg.Any<CancellationToken>())
                    .Returns(new GitWriterBackportRegeneratedResult("branch-moved", null, null, null, "the head is now aaaa", 1));
                break;
            case "access-denied": DispatchThrows(new ForgeAccessDeniedException("403", "actions: write")); break;
            case "access-denied-bare": DispatchThrows(new ForgeAccessDeniedException("403")); break;
            case "writer-refused": DispatchThrows(new GitWriterRefusedException("backport/regenerated", "patch", "base64")); break;
        }
        await BeginAsync(store, Item("regenerate.yml"));

        await StepAsync(store); // dispatch
        if (forge.OpenedPullRequests.Count == 0)
            await StepAsync(store, Completed((await PendingAsync(store))!));

        forge.OpenedPullRequests.Single().Body.Should().Contain($"**Not regenerated**: {why}. These files kept the line's version");
        forge.Comments.Single().Body.Should().Be($"Backport to `release/8.x`: #1000, **not regenerated**: {why}.");
        (await PendingAsync(store)).Should().BeNull();
    }

    [Fact]
    public async Task A_run_still_going_waits_and_a_poll_reads_it_too()
    {
        using var store = GetDocumentStore();
        await BeginAsync(store, Item("regenerate.yml"));
        await StepAsync(store);
        var item = (await PendingAsync(store))!;
        hook.ObserveAsync(Arg.Any<IWorkflowHookSaga>(), Arg.Any<WorkflowHookRun>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new WorkflowHookObservation(EWorkflowHookRunVerdict.Running, null));

        await StepAsync(store, new BackportRegenerationStepMessage { RepositoryId = RepositoryId, Head = Head, Generation = item.Generation, Step = EBackportRegenerationStep.PollRun });
        await StepAsync(store, Completed(item));
        // A dispatch step while the run is pending starts nothing.
        await StepAsync(store, new BackportRegenerationStepMessage { RepositoryId = RepositoryId, Head = Head, Generation = item.Generation, Step = EBackportRegenerationStep.Dispatch });

        await hook.Received(1).ObserveAsync(Arg.Any<IWorkflowHookSaga>(), Arg.Any<WorkflowHookRun>(), Backport, true, Arg.Any<CancellationToken>());
        await hook.Received(1).ObserveAsync(Arg.Any<IWorkflowHookSaga>(), Arg.Any<WorkflowHookRun>(), Backport, false, Arg.Any<CancellationToken>());
        dispatches.Should().ContainSingle();
        forge.OpenedPullRequests.Should().BeEmpty();
        (await PendingAsync(store))!.Runs.Single().Finished.Should().BeFalse();
    }

    [Fact]
    public async Task A_run_with_nothing_to_regenerate_opens_the_pull_request_saying_so()
    {
        using var store = GetDocumentStore();
        hook.DownloadArtifactAsync(Arg.Any<IWorkflowHookSaga>(), Arg.Any<long>(), "merge-queue-patch", Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new ForgeArtifactDownload(EForgeArtifactOutcome.Ok, "patch.diff", [], null));
        await BeginAsync(store, Item("regenerate.yml"));

        await StepAsync(store);
        await StepAsync(store, Completed((await PendingAsync(store))!));
        (await PendingAsync(store))!.History.Should().Contain(l => l.Text =="regenerate.yml run 41 had nothing to regenerate");
        await StepAsync(store);

        await writer.DidNotReceiveWithAnyArgs().VersioningBackportRegeneratedAsync(default!, default);
        forge.OpenedPullRequests.Single().Body.Should().EndWith("folded into the backport commit:\n- nothing changed\n");
    }

    [Fact]
    public async Task A_transient_failure_is_retried_until_it_gives_up()
    {
        using var store = GetDocumentStore();
        hook.DispatchAsync(Arg.Any<IWorkflowHookSaga>(), Arg.Any<WorkflowHookRequest>(), Arg.Any<Func<WorkflowHookDispatch, Task>>(), Arg.Any<CancellationToken>())
            .Returns<WorkflowHookDispatch>(_ => throw new HttpRequestException("connection reset"));
        await BeginAsync(store, Item("regenerate.yml"));

        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            await StepAsync(store);
            (await PendingAsync(store))!.TransientFailures.Should().Be(attempt);
            outbox.Steps.Last().Step.Should().Be(EBackportRegenerationStep.Dispatch);
        }
        forge.OpenedPullRequests.Should().BeEmpty();

        await StepAsync(store);

        forge.Comments.Single().Body.Should().Be(
            $"Backport to `release/8.x`: #1000, **not regenerated**: it gave up after {MaxRetries + 1} failed attempts in a row (connection reset).");
    }

    private const int MaxRetries = BackportRegenerationEngine.MaxTransientRetries;

    [Fact]
    public async Task A_rate_limit_is_left_to_the_message_bus_and_saves_nothing()
    {
        using var store = GetDocumentStore();
        hook.DispatchAsync(Arg.Any<IWorkflowHookSaga>(), Arg.Any<WorkflowHookRequest>(), Arg.Any<Func<WorkflowHookDispatch, Task>>(), Arg.Any<CancellationToken>())
            .Returns<WorkflowHookDispatch>(_ => throw new ForgeRateLimitedException(TimeSpan.FromMinutes(1), null, EForgeRateLimitKind.Secondary, "slow down"));
        await BeginAsync(store, Item("regenerate.yml"));

        var act = () => StepAsync(store);

        await act.Should().ThrowAsync<ForgeRateLimitedException>();
        (await PendingAsync(store))!.TransientFailures.Should().Be(0);
        forge.OpenedPullRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_cancel_step_cancels_the_run_even_when_the_backport_is_gone()
    {
        using var store = GetDocumentStore();
        IWorkflowHookSaga? saga = null;
        await hook.CancelAbandonedRunAsync(Arg.Do<IWorkflowHookSaga>(s => saga = s), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
        await BeginAsync(store, Item("regenerate.yml"));

        await StepAsync(store, new BackportRegenerationStepMessage { RepositoryId = RepositoryId, Head = "backport/gone-8.x", Generation = 99, Step = EBackportRegenerationStep.CancelRun, RunId = 41 });
        await StepAsync(store, new BackportRegenerationStepMessage { RepositoryId = RepositoryId, Head = Head, Generation = 0, Step = EBackportRegenerationStep.CancelRun });

        await hook.Received(1).CancelAbandonedRunAsync(Arg.Any<IWorkflowHookSaga>(), BackportRegenerationEngine.Purpose, 41, Arg.Any<CancellationToken>());
        saga!.Repository.FullName.Should().Be("acme/widgets");
        saga.Forge.Should().BeSameAs(queue);
        saga.Note("ignored");
        await saga.ScheduleAsync(EWorkflowHookStep.PollRun, TimeSpan.FromMinutes(1), null, default);
        outbox.Steps.Should().ContainSingle("only the first dispatch was queued");
    }

    [Fact]
    public async Task A_step_for_an_unknown_repository_or_forge_does_nothing()
    {
        using var store = GetDocumentStore();
        await BeginAsync(store, Item("regenerate.yml"));
        queue.Provider.Returns(EForgeProvider.GitLab);

        await StepAsync(store);
        await StepAsync(store, new BackportRegenerationStepMessage { RepositoryId = "Repositories/github/404", Head = Head, Generation = 0, Step = EBackportRegenerationStep.Dispatch });

        dispatches.Should().BeEmpty();
        await hook.DidNotReceiveWithAnyArgs().VerifyProvenanceAsync(default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task The_saga_schedules_the_hooks_steps_for_this_backport()
    {
        using var store = GetDocumentStore();
        IWorkflowHookSaga? saga = null;
        hook.VerifyProvenanceAsync(Arg.Do<IWorkflowHookSaga>(s => saga = s), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns((WorkflowHookProvenanceFailure?)null);
        await BeginAsync(store, Item("regenerate.yml"));
        await StepAsync(store);

        saga!.Repository.FullName.Should().Be("acme/widgets");
        saga.Forge.Should().BeSameAs(queue);
        saga.Note("looked at the run");
        await saga.ScheduleAsync(EWorkflowHookStep.PollRun, TimeSpan.FromMinutes(1), null, default);
        await saga.ScheduleAsync(EWorkflowHookStep.Timeout, TimeSpan.FromMinutes(20), null, default);
        await saga.ScheduleAsync(EWorkflowHookStep.CancelRun, null, 41, default);
        var unknown = () => saga.ScheduleAsync((EWorkflowHookStep)99, null, null, default);

        outbox.Steps.TakeLast(3).Select(s => (s.Step, s.Head, s.RunId)).Should().Equal(
            (EBackportRegenerationStep.PollRun, Head, (long?)null), (EBackportRegenerationStep.Timeout, Head, null), (EBackportRegenerationStep.CancelRun, Head, 41));
        await unknown.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("none", "the default branch's `coverage.yml` has no usable `mergeQueue` section")]
    [InlineData("never", "`mergeQueue.postRebaseWhen` is `never`")]
    [InlineData("no-workflow", "`mergeQueue.postRebaseActionName` names no regenerate workflow")]
    [InlineData("too-many", "its 65 regenerated paths exceed the git writer's 64")]
    public void The_plan_refuses_what_it_cannot_run(string kind, string refusal)
    {
        MergeQueueWireRules.MaxGlobs.Should().Be(64, "the refusal text above names the cap");
        var paths = kind == "too-many" ? Enumerable.Range(0, MergeQueueWireRules.MaxGlobs + 1).Select(i => $"app{i}/package-lock.json").ToList() : ["app/package-lock.json"];
        var section = kind switch
        {
            "none" => null,
            "never" => new MergeQueueSection { PostRebaseActionName = "regenerate.yml", PostRebaseWhen = EPostRebaseWhen.Never },
            "no-workflow" => new MergeQueueSection(),
            _ => new MergeQueueSection { PostRebaseActionName = "regenerate.yml" },
        };
        var target = new GitWriterBackportTargetResult("release/8.x", Head, Backport, GitWriterBackportTargetResult.Created, [], Regenerate: paths);

        var plan = BackportRegenerationEngine.Plan(section, target);

        plan.Refusal.Should().Be(refusal);
        plan.Workflows.Should().BeEmpty();
    }

    [Fact]
    public async Task A_finished_run_is_forwarded_to_the_backport_waiting_for_it()
    {
        using var store = GetDocumentStore();
        await BeginAsync(store, Item("regenerate.yml"));
        await StepAsync(store);
        var bus = new RecordingMessageBus();

        foreach (var (repository, run) in new[] { (RepositoryId, 41L), (RepositoryId, 99L), ("github/153", 41L) })
        {
            using var session = store.OpenAsyncSession();
            await new BackportRegenerationEventsRecipient(session, bus)
                .HandleAsync(new ForgeWebhookMessage<CiRunCompleted>(EForgeProvider.GitHub, new CiRunCompleted(repository, run, Backport, "success")));
        }

        var item = (await PendingAsync(store))!;
        bus.Of<BackportRegenerationStepMessage>().Should().ContainSingle().Which.Should().BeEquivalentTo(new BackportRegenerationStepMessage
        {
            RepositoryId = RepositoryId, Head = Head, Generation = item.Generation, Step = EBackportRegenerationStep.RunCompleted, RunId = 41,
        });

        var engine = Substitute.For<IBackportRegenerationEngine>();
        var step = bus.Of<BackportRegenerationStepMessage>().Single();
        await new BackportRegenerationRecipient(engine).HandleAsync(step);
        await engine.Received(1).HandleStepAsync(step, Arg.Any<CancellationToken>());
    }

    private sealed class Outbox : IMessageOutbox
    {
        public List<BackportRegenerationStepMessage> Steps { get; } = [];
        public List<MergeQueueSignalMessage> Signals { get; } = [];

        public Task EnqueueAsync<TMessage>(IAsyncDocumentSession session, TMessage message, BroadcastOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (message is BackportRegenerationStepMessage step) Steps.Add(step);
            if (message is MergeQueueSignalMessage signal) Signals.Add(signal);
            return Task.CompletedTask;
        }
    }
}
