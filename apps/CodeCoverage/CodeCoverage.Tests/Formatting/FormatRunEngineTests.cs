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
            RepositoryId, Kind = EMergeQueueSignal.Command, Number = 12, Command = EMergeQueueCommand.Enqueue, Actor = "octocat", Source = EMergeQueueCommandSource.Formatting,
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

    private sealed class Outbox : IMessageOutbox
    {
        public List<FormatRunStepMessage> Steps { get; } = [];
        public List<MergeQueueSignalMessage> Signals { get; } = [];

        public Task EnqueueAsync<TMessage>(IAsyncDocumentSession session, TMessage message, BroadcastOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (message is FormatRunStepMessage step) Steps.Add(step);
            if (message is MergeQueueSignalMessage signal) Signals.Add(signal);
            return Task.CompletedTask;
        }
    }
}
