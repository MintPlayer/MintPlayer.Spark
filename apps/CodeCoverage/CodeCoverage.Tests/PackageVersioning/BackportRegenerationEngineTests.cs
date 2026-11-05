using CodeCoverage.CommitGraph;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
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

    private sealed class Outbox : IMessageOutbox
    {
        public List<BackportRegenerationStepMessage> Steps { get; } = [];

        public Task EnqueueAsync<TMessage>(IAsyncDocumentSession session, TMessage message, BroadcastOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (message is BackportRegenerationStepMessage step) Steps.Add(step);
            return Task.CompletedTask;
        }
    }
}
