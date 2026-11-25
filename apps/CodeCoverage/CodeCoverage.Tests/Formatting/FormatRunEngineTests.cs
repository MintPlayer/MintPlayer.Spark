using System.Text;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Formatting;
using CodeCoverage.MergeQueue;
using CodeCoverage.Onboarding;
using CodeCoverage.WorkflowHooks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Notifications;
using NSubstitute;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.Formatting;

/// <summary>
/// #657 M4/M5/M8 (docs/coverage_file_formatter_PRD.md D1, D5, D14, D15): a FormatRepository or LintRepository run dispatches
/// the repository's own <c>formatting.yml</c>, guards its patch, opens the pull request, hands it to the merge queue when
/// asked, and tells the person who asked.
/// </summary>
public class FormatRunEngineTests : CoverageRavenTest
{
    private const string RepositoryId = "Repositories/github/657";
    private const string User = "Users/1-A";
    private static readonly string Sha = new('a', 40);

    private static readonly byte[] Patch = Encoding.UTF8.GetBytes("""
        diff --git a/src/A.cs b/src/A.cs
        index 1111111..2222222 100644
        --- a/src/A.cs
        +++ b/src/A.cs
        @@ -1 +1 @@
        -class A{}
        +class A { }

        """.ReplaceLineEndings("\n"));

    private readonly IForgeMergeQueue queue = Substitute.For<IForgeMergeQueue>();
    private readonly IOnboardingService onboarding = Substitute.For<IOnboardingService>();
    private readonly IGitWriterSidecar writer = Substitute.For<IGitWriterSidecar>();
    private readonly IWorkflowHook hook = Substitute.For<IWorkflowHook>();
    private readonly ISparkNotifier notifier = Substitute.For<ISparkNotifier>();
    private readonly Outbox outbox = new();
    private readonly List<WorkflowHookRequest> dispatches = [];
    private readonly List<SparkNotification> notifications = [];
    private byte[] patch = Patch;

    public FormatRunEngineTests()
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
                var dispatched = new WorkflowHookDispatch(EWorkflowHookDispatch.Dispatched, request.Workflow, 41, "https://example.invalid/run/41", EForgeDispatchOutcome.Dispatched, null);
                await call.ArgAt<Func<WorkflowHookDispatch, Task>>(2)(dispatched);
                return dispatched;
            });
        hook.ObserveAsync(Arg.Any<IWorkflowHookSaga>(), Arg.Any<WorkflowHookRun>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var run = call.ArgAt<WorkflowHookRun>(1);
                run.Finished = run.Succeeded = true;
                return new WorkflowHookObservation(EWorkflowHookRunVerdict.Succeeded, new ForgeWorkflowRun(run.RunId!.Value, "completed", "success", call.ArgAt<string>(2), "main", null));
            });
        hook.DownloadArtifactAsync(Arg.Any<IWorkflowHookSaga>(), Arg.Any<long>(), "merge-queue-patch", Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ForgeArtifactDownload(EForgeArtifactOutcome.Ok, "patch.diff", patch, null));
        hook.DownloadArtifactAsync(Arg.Any<IWorkflowHookSaga>(), Arg.Any<long>(), FormattingWorkflow.ReportArtifactName, Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new ForgeArtifactDownload(EForgeArtifactOutcome.Ok, "format-report.json", Encoding.UTF8.GetBytes("""{"tools":[{"id":"dotnet-format","files":1}]}"""), null));
        onboarding.FormattingPullRequestAsync(Arg.Any<Repository>(), Arg.Any<FormattingCommit>(), Arg.Any<CancellationToken>())
            .Returns(new FormattingPullRequest(true, "Opened #12", "https://github.com/acme/widgets/pull/12", 12));
        notifier.NotifyAsync(Arg.Do<SparkNotification>(notifications.Add), Arg.Any<CancellationToken>()).Returns(new SparkNotifyReceipt([], 0, 0));
    }

    private FormatRunEngine Engine(IAsyncDocumentSession session)
        => new(session, [queue], onboarding, writer, outbox, hook, notifier, TimeProvider.System, Options.Create(new MergeQueueOptions()), NullLogger<FormatRunEngine>.Instance);

    private static FormatRun Run(string mode = "format", bool enqueue = false, params string[] exclude) => new()
    {
        Mode = mode, RequestedBy = User, RequestedByLogin = enqueue ? "octocat" : null, Enqueue = enqueue, Branch = "main", Sha = Sha, Exclude = [.. exclude],
    };

    private async Task<bool> StartAsync(IDocumentStore store, FormatRun run)
    {
        using (var seed = store.OpenAsyncSession())
        {
            if (await seed.LoadAsync<Repository>(RepositoryId) is null)
            {
                await seed.StoreAsync(new Repository { FullName = "acme/widgets", Provider = EForgeProvider.GitHub, GitHubId = 657, DefaultBranch = "main" }, RepositoryId);
                await seed.SaveChangesAsync();
            }
        }
        using var session = store.OpenAsyncSession();
        return await Engine(session).StartAsync((await session.LoadAsync<Repository>(RepositoryId))!, run);
    }

    private async Task StepAsync(IDocumentStore store, FormatRunStepMessage? step = null)
    {
        using var session = store.OpenAsyncSession();
        await Engine(session).HandleStepAsync(step ?? outbox.Steps.Last());
    }

    private static async Task<FormatRun?> PendingAsync(IDocumentStore store, string mode = "format")
    {
        using var session = store.OpenAsyncSession();
        return (await session.LoadAsync<FormatRuns>(FormatRuns.ForRepository(RepositoryId)))?.Pending.SingleOrDefault(p => p.Mode == mode);
    }

    private async Task RunToCompletionAsync(IDocumentStore store, string mode = "format")
    {
        await StepAsync(store); // dispatch
        var pending = (await PendingAsync(store, mode))!;
        await StepAsync(store, new FormatRunStepMessage { RepositoryId = RepositoryId, Mode = mode, Generation = pending.Generation, Step = EFormatRunStep.RunCompleted, RunId = 41 });
    }

    [Fact]
    public async Task M4_the_run_opens_the_pull_request_and_tells_who_asked()
    {
        using var store = GetDocumentStore();
        (await StartAsync(store, Run())).Should().BeTrue();

        await RunToCompletionAsync(store);

        var request = dispatches.Single();
        (request.Workflow, request.Ref, request.Inputs["mode"], request.Inputs["expected_sha"]).Should().Be(("formatting.yml", "main", "format", Sha));
        await onboarding.Received(1).FormattingPullRequestAsync(Arg.Any<Repository>(), Arg.Is<FormattingCommit>(c =>
            c.Mode == "format" && c.BaseSha == Sha && c.Title == "Format the repository" && c.Patch.SequenceEqual(Patch) && !c.PushesWorkflowChanges
            && c.Body.Contains("| `dotnet-format` |") && c.Body.Contains("([run](https://example.invalid/run/41))")), Arg.Any<CancellationToken>());
        outbox.Signals.Should().BeEmpty();
        notifications.Single().Should().BeEquivalentTo(new
        {
            UserIds = new[] { User }, Title = "Format of acme/widgets: pull request opened", Url = "https://github.com/acme/widgets/pull/12", Category = "format-run",
        });
        (await PendingAsync(store)).Should().BeNull();
    }

    [Fact]
    public async Task D5_a_queued_run_hands_the_pull_request_to_the_merge_queue_as_the_person_who_asked()
    {
        using var store = GetDocumentStore();
        await StartAsync(store, Run(enqueue: true));

        await RunToCompletionAsync(store);

        outbox.Signals.Single().Should().BeEquivalentTo(new
        {
            RepositoryId = RepositoryId.ToLowerInvariant(), // the loaded repository's id (#536 D7)
            Kind = EMergeQueueSignal.Command, Number = 12, Command = EMergeQueueCommand.Enqueue, Actor = "octocat", Source = EMergeQueueCommandSource.Formatting,
        });
        notifications.Single().Body.Should().Contain("handed to the merge queue");
    }

    [Fact]
    public async Task D14_an_empty_patch_opens_nothing_and_says_so()
    {
        using var store = GetDocumentStore();
        patch = [];
        await StartAsync(store, Run("lint"));

        await RunToCompletionAsync(store, "lint");

        await onboarding.DidNotReceiveWithAnyArgs().FormattingPullRequestAsync(default!, default!, default);
        notifications.Single().Should().BeEquivalentTo(new { Title = "Lint of acme/widgets: nothing to change", Body = "The linters had nothing to fix." });
        (await PendingAsync(store, "lint")).Should().BeNull();
    }

    [Fact]
    public async Task D15_a_patch_on_an_excluded_file_is_refused_before_the_writer_sees_it()
    {
        using var store = GetDocumentStore();
        await StartAsync(store, Run(exclude: "src/"));

        await RunToCompletionAsync(store);

        await onboarding.DidNotReceiveWithAnyArgs().FormattingPullRequestAsync(default!, default!, default);
        notifications.Single().Title.Should().Be("Format of acme/widgets: no pull request");
        notifications.Single().Body.Should().Contain("Its patch was refused").And.Contain("src/A.cs");
    }

    [Fact]
    public async Task D1_a_branch_without_the_workflow_is_refused()
    {
        using var store = GetDocumentStore();
        hook.VerifyProvenanceAsync(Arg.Any<IWorkflowHookSaga>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new WorkflowHookProvenanceFailure(EWorkflowHookProvenance.Missing, FormattingWorkflow.Path));
        await StartAsync(store, Run());

        await StepAsync(store);

        dispatches.Should().BeEmpty();
        notifications.Single().Body.Should().Contain("set up Formatting first");
        (await PendingAsync(store)).Should().BeNull();
    }

    [Fact]
    public async Task M4_one_run_per_mode_at_a_time()
    {
        using var store = GetDocumentStore();
        (await StartAsync(store, Run())).Should().BeTrue();

        (await StartAsync(store, Run())).Should().BeFalse("a format run is still going");
        (await StartAsync(store, Run("lint"))).Should().BeTrue("lint is its own run");
    }

    [Fact]
    public async Task M4_a_step_of_an_older_generation_is_ignored()
    {
        using var store = GetDocumentStore();
        await StartAsync(store, Run());
        var dispatch = outbox.Steps.Last();
        await StepAsync(store, dispatch);

        await StepAsync(store, dispatch); // the dispatch again, after the run started

        dispatches.Should().ContainSingle();
    }

    private async Task<string> FailureAsync(IDocumentStore store)
    {
        (await PendingAsync(store)).Should().BeNull("a failed run is forgotten");
        await onboarding.DidNotReceiveWithAnyArgs().FormattingPullRequestAsync(default!, default!, default);
        notifications.Single().Title.Should().Be("Format of acme/widgets: no pull request");
        return notifications.Single().Body;
    }

    private void Observe(EWorkflowHookRunVerdict verdict, string? conclusion = null, string? headSha = null, string? htmlUrl = null)
        => hook.ObserveAsync(Arg.Any<IWorkflowHookSaga>(), Arg.Any<WorkflowHookRun>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new WorkflowHookObservation(verdict, verdict == EWorkflowHookRunVerdict.Missing ? null
                : new ForgeWorkflowRun(41, "completed", conclusion, headSha ?? Sha, "main", htmlUrl)));

    [Fact]
    public async Task M5_a_failed_run_says_how_it_finished_and_links_the_run()
    {
        using var store = GetDocumentStore();
        Observe(EWorkflowHookRunVerdict.Failed, "failure", htmlUrl: "https://example.invalid/run/41/attempt");
        await StartAsync(store, Run());

        await RunToCompletionAsync(store);

        (await FailureAsync(store)).Should().Be("The `formatting.yml` run finished `failure`.");
        notifications.Single().Url.Should().Be("https://example.invalid/run/41/attempt");
        await hook.Received(1).AbandonAsync(Arg.Any<IWorkflowHookSaga>(), Arg.Any<IEnumerable<WorkflowHookRun>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task M5_a_run_on_another_commit_asks_to_run_again()
    {
        using var store = GetDocumentStore();
        Observe(EWorkflowHookRunVerdict.WrongCommit, "success", headSha: new string('b', 40));
        await StartAsync(store, Run());

        await RunToCompletionAsync(store);

        (await FailureAsync(store)).Should().Be("The run formatted `bbbbbbbbbbbb`, but `main` was at `aaaaaaaaaaaa`; `main` moved, run it again.");
    }

    [Fact]
    public async Task M5_a_vanished_run_is_reported()
    {
        using var store = GetDocumentStore();
        Observe(EWorkflowHookRunVerdict.Missing);
        await StartAsync(store, Run());

        await RunToCompletionAsync(store);

        (await FailureAsync(store)).Should().Be("The `formatting.yml` run 41 no longer exists.");
    }

    [Fact]
    public async Task M5_a_run_still_going_waits()
    {
        using var store = GetDocumentStore();
        Observe(EWorkflowHookRunVerdict.Running);
        await StartAsync(store, Run());

        await RunToCompletionAsync(store);

        notifications.Should().BeEmpty();
        (await PendingAsync(store))!.Run!.RunId.Should().Be(41);
    }

    [Fact]
    public async Task M5_an_unusable_artifact_fails_the_run()
    {
        using var store = GetDocumentStore();
        hook.DownloadArtifactAsync(Arg.Any<IWorkflowHookSaga>(), Arg.Any<long>(), "merge-queue-patch", Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new ForgeArtifactDownload(EForgeArtifactOutcome.NotFound, null, null, "no such artifact"));
        await StartAsync(store, Run());

        await RunToCompletionAsync(store);

        (await FailureAsync(store)).Should().Be("The run left an unusable `merge-queue-patch` artifact (NotFound: no such artifact).");
    }

    [Fact]
    public async Task M5_a_pull_request_that_could_not_open_says_why()
    {
        using var store = GetDocumentStore();
        onboarding.FormattingPullRequestAsync(Arg.Any<Repository>(), Arg.Any<FormattingCommit>(), Arg.Any<CancellationToken>())
            .Returns(new FormattingPullRequest(false, "the base branch moved", null, null));
        await StartAsync(store, Run(enqueue: true));

        await RunToCompletionAsync(store);

        notifications.Single().Body.Should().Be("The base branch moved.");
        outbox.Signals.Should().BeEmpty("nothing opened, so nothing is queued");
        (await PendingAsync(store)).Should().BeNull();
    }

    [Fact]
    public async Task M4_the_timeout_ends_the_run()
    {
        using var store = GetDocumentStore();
        Observe(EWorkflowHookRunVerdict.Running);
        await StartAsync(store, Run());
        await StepAsync(store);
        var pending = (await PendingAsync(store))!;

        await StepAsync(store, new FormatRunStepMessage { RepositoryId = RepositoryId, Mode = "format", Generation = pending.Generation, Step = EFormatRunStep.Timeout });

        (await FailureAsync(store)).Should().Be("The `formatting.yml` run took longer than 30 minutes.");
        await hook.Received(1).AbandonAsync(Arg.Any<IWorkflowHookSaga>(), Arg.Any<IEnumerable<WorkflowHookRun>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task D1_a_copy_that_differs_from_the_default_branch_is_refused()
    {
        using var store = GetDocumentStore();
        hook.VerifyProvenanceAsync(Arg.Any<IWorkflowHookSaga>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new WorkflowHookProvenanceFailure(EWorkflowHookProvenance.Differs, FormattingWorkflow.Path));
        await StartAsync(store, Run());

        await StepAsync(store);

        dispatches.Should().BeEmpty();
        (await FailureAsync(store)).Should().Be("`.github/workflows/formatting.yml` differs from `main`'s copy.");
    }

    [Fact]
    public async Task M4_without_a_git_writer_nothing_is_dispatched()
    {
        using var store = GetDocumentStore();
        writer.IsConfigured.Returns(false);
        await StartAsync(store, Run());

        await StepAsync(store);

        dispatches.Should().BeEmpty();
        (await FailureAsync(store)).Should().Be("This server's git writer is not configured.");
    }

    [Theory]
    [InlineData(EWorkflowHookDispatch.Refused, "The forge refused to run `formatting.yml` on `main` (WorkflowNotFound: Not Found).")]
    [InlineData(EWorkflowHookDispatch.Uncorrelated, "The forge started `formatting.yml` without saying which run it is, so its result cannot be trusted.")]
    public async Task M4_a_dispatch_that_did_not_start_a_known_run_fails(EWorkflowHookDispatch outcome, string body)
    {
        using var store = GetDocumentStore();
        hook.DispatchAsync(Arg.Any<IWorkflowHookSaga>(), Arg.Any<WorkflowHookRequest>(), Arg.Any<Func<WorkflowHookDispatch, Task>>(), Arg.Any<CancellationToken>())
            .Returns(new WorkflowHookDispatch(outcome, FormattingWorkflow.FileName, null, null, EForgeDispatchOutcome.WorkflowNotFound, "Not Found"));
        await StartAsync(store, Run());

        await StepAsync(store);

        (await FailureAsync(store)).Should().Be(body);
    }

    [Fact]
    public async Task M4_a_permission_the_forge_refuses_is_named()
    {
        using var store = GetDocumentStore();
        hook.DispatchAsync(Arg.Any<IWorkflowHookSaga>(), Arg.Any<WorkflowHookRequest>(), Arg.Any<Func<WorkflowHookDispatch, Task>>(), Arg.Any<CancellationToken>())
            .Returns<WorkflowHookDispatch>(_ => throw new ForgeAccessDeniedException("Resource not accessible by integration", "actions: write"));
        await StartAsync(store, Run());

        await StepAsync(store);

        (await FailureAsync(store)).Should().Be("The forge refused a call the run needs (it asks for `actions: write`).");
    }

    [Fact]
    public async Task M5_a_request_the_git_writer_refuses_is_named()
    {
        using var store = GetDocumentStore();
        onboarding.FormattingPullRequestAsync(Arg.Any<Repository>(), Arg.Any<FormattingCommit>(), Arg.Any<CancellationToken>())
            .Returns<FormattingPullRequest>(_ => throw new GitWriterRefusedException("onboarding", "format.exclude", "a list of globs"));
        await StartAsync(store, Run());

        await RunToCompletionAsync(store);

        notifications.Single().Body.Should().Be("The git writer refused the onboarding request (`format.exclude` must be a list of globs).");
        (await PendingAsync(store)).Should().BeNull();
    }

    [Fact]
    public async Task M4_a_transient_failure_retries_the_step_later_then_gives_up()
    {
        using var store = GetDocumentStore();
        hook.DispatchAsync(Arg.Any<IWorkflowHookSaga>(), Arg.Any<WorkflowHookRequest>(), Arg.Any<Func<WorkflowHookDispatch, Task>>(), Arg.Any<CancellationToken>())
            .Returns<WorkflowHookDispatch>(_ => throw new HttpRequestException("connection reset"));
        await StartAsync(store, Run());

        await StepAsync(store);

        var retry = outbox.Steps.Last();
        (retry.Step, outbox.Options.Last()?.Delay).Should().Be((EFormatRunStep.Dispatch, TimeSpan.FromSeconds(15)));
        (await PendingAsync(store))!.TransientFailures.Should().Be(1);
        notifications.Should().BeEmpty();

        for (var attempt = 2; attempt <= FormatRunEngine.MaxTransientRetries + 1; attempt++)
            await StepAsync(store);

        (await FailureAsync(store)).Should().Be($"It gave up after {FormatRunEngine.MaxTransientRetries + 1} failed attempts in a row (connection reset).");
    }

    [Fact]
    public async Task M4_a_stale_run_is_replaced_by_a_new_request()
    {
        using var store = GetDocumentStore();
        await StartAsync(store, Run());
        using (var session = store.OpenAsyncSession())
        {
            var document = await session.LoadAsync<FormatRuns>(FormatRuns.ForRepository(RepositoryId));
            document.Pending.Single().StartedUtc = DateTime.UtcNow - FormatRunEngine.Stale - TimeSpan.FromMinutes(1);
            document.Pending.Single().Sha = "old";
            await session.SaveChangesAsync();
        }

        (await StartAsync(store, Run())).Should().BeTrue("its timeout step was lost");

        (await PendingAsync(store))!.Sha.Should().Be(Sha);
    }

    [Fact]
    public async Task M4_an_abandoned_run_is_cancelled_whatever_the_generation()
    {
        using var store = GetDocumentStore();
        await StartAsync(store, Run());

        await StepAsync(store, new FormatRunStepMessage { RepositoryId = RepositoryId, Mode = "format", Generation = 99, Step = EFormatRunStep.CancelRun, RunId = 40 });

        await hook.Received(1).CancelAbandonedRunAsync(Arg.Any<IWorkflowHookSaga>(), FormatRunEngine.Purpose, 40, Arg.Any<CancellationToken>());
        dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task M4_a_finished_workflow_run_wakes_only_the_format_run_waiting_for_it()
    {
        using var store = GetDocumentStore();
        await StartAsync(store, Run());
        await StepAsync(store);
        var bus = Substitute.For<IMessageBus>();
        using var session = store.OpenAsyncSession();
        var recipient = new FormatRunEventsRecipient(session, bus);

        await recipient.HandleAsync(new ForgeWebhookMessage<CiRunCompleted>(EForgeProvider.GitHub, new CiRunCompleted(RepositoryId, 41, Sha, "success")));
        await recipient.HandleAsync(new ForgeWebhookMessage<CiRunCompleted>(EForgeProvider.GitHub, new CiRunCompleted(RepositoryId, 77, Sha, "success")));
        await recipient.HandleAsync(new ForgeWebhookMessage<CiRunCompleted>(EForgeProvider.GitHub, new CiRunCompleted("Projects/1", 41, Sha, "success")));

        var generation = (await PendingAsync(store))!.Generation;
        await bus.Received(1).BroadcastAsync(Arg.Any<FormatRunStepMessage>(), Arg.Any<CancellationToken>());
        await bus.Received(1).BroadcastAsync(Arg.Is<FormatRunStepMessage>(m =>
            m.RepositoryId == RepositoryId && m.Mode == "format" && m.Generation == generation && m.Step == EFormatRunStep.RunCompleted && m.RunId == 41), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task M4_the_step_recipient_hands_the_step_to_the_engine()
    {
        var engine = Substitute.For<IFormatRunEngine>();
        var step = new FormatRunStepMessage { RepositoryId = RepositoryId, Mode = "format", Generation = 1, Step = EFormatRunStep.PollRun };

        await new FormatRunRecipient(engine).HandleAsync(step);

        await engine.Received(1).HandleStepAsync(step, Arg.Any<CancellationToken>());
    }

    private sealed class Outbox : IMessageOutbox
    {
        public List<FormatRunStepMessage> Steps { get; } = [];
        public List<MergeQueueSignalMessage> Signals { get; } = [];
        public List<BroadcastOptions?> Options { get; } = [];

        public Task EnqueueAsync<TMessage>(IAsyncDocumentSession session, TMessage message, BroadcastOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (message is FormatRunStepMessage step)
            {
                Steps.Add(step);
                Options.Add(options);
            }
            if (message is MergeQueueSignalMessage signal) Signals.Add(signal);
            return Task.CompletedTask;
        }
    }
}
