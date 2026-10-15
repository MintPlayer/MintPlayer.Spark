using System.Security.Claims;
using System.Text.Json;
using CodeCoverage.Controllers;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using CodeCoverage.Tests.MergeQueue;
using CodeCoverage.Tests.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;
using QueueDocument = CodeCoverage.Entities.MergeQueue;

namespace CodeCoverage.Tests.Controllers;

/// <summary>
/// The repository page's merge-queue panel and button (merge-queue PRD M6, O8, O15, T1): what the panel reads, and that the
/// button raises the queue's one neutral command as the viewer's verified forge login, or refuses with a reason. The role
/// check itself is the engine's (O9, <see cref="MergeQueueEngineTests"/>). The refusal parity with the browse API is in
/// <see cref="RepositoryExistenceParityTests"/>; anonymous callers and CSRF are the pipeline's
/// (<see cref="SparkAuthorizeEndToEndTests"/>, <c>CsrfSurfaceTests</c>, the route snapshot).
/// </summary>
public class MergeQueueControllerTests : CoverageRavenTest
{
    protected override bool DeployIndexes => true;

    private const long RepoGitHubId = 910040;
    private static readonly string RepositoryId = Repository.DocumentId(EForgeProvider.GitHub, RepoGitHubId);

    private sealed class RecordingMessageBus : IMessageBus
    {
        public List<object> Messages { get; } = [];

        public Task BroadcastAsync<TMessage>(TMessage message, BroadcastOptions options, CancellationToken cancellationToken = default)
            => BroadcastAsync(message, cancellationToken);

        public Task BroadcastAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
        {
            Messages.Add(message!);
            return Task.CompletedTask;
        }

        public Task BroadcastOnceAsync<TMessage>(TMessage message, string deduplicationKey, CancellationToken cancellationToken = default)
            => BroadcastAsync(message, cancellationToken);

        public Task DelayBroadcastAsync<TMessage>(TMessage message, TimeSpan delay, CancellationToken cancellationToken = default)
            => BroadcastAsync(message, cancellationToken);

        public IReadOnlyList<MergeQueueCommandIssued> Commands
            => [.. Messages.OfType<ForgeWebhookMessage<MergeQueueCommandIssued>>().Select(m => m.Event)];
    }

    /// <summary>The world one test runs in: who the viewer is to GitHub, and what the forge and the queue answer.</summary>
    private sealed class World
    {
        public GitHubVerifiedLogin? Verified { get; set; }
        public string[] Owners { get; set; } = [];
        public bool QueueSupported { get; set; } = true;
        public FakeForgeMergeQueue Queue { get; } = new();
        public RecordingMessageBus Bus { get; } = new();
        public Dictionary<int, ForgePullRequest> PullRequests { get; } = [];
    }

    private static MergeQueueController Controller(IAsyncDocumentSession session, World world, ClaimsPrincipal? user = null)
    {
        var visibility = new GitHubVisibility(world.Owners, GitHubTokenState.Ok);
        var forge = ScriptedForge.From(visibility);
        foreach (var (number, pull) in world.PullRequests) forge.PullRequests[number] = pull;

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddMemoryCache();
        services.AddSingleton(session);
        services.AddSingleton<IRepositoryResolver>(new TestRepositoryResolver(session));
        services.AddSingleton<IForgeIntegration>(forge);
        services.AddSingleton<IForgeIntegrationResolver>(forge);
        services.AddSingleton<IGitHubAccessService>(new ScriptedAccessService(visibility, world.Verified));
        // The real adapter: the login the button acts as is the one IGitHubAccessService verified, mapped like production.
        services.AddScoped<IForgeAccessService, GitHubForgeAccessService>();
        if (world.QueueSupported) services.AddSingleton<IForgeMergeQueue>(world.Queue);
        services.AddSingleton<IMessageBus>(world.Bus);
        services.AddScoped<IRepositoryScope, RepositoryScope>();
        services.AddScoped<MergeQueueController>();
        var provider = services.BuildServiceProvider();

        var controller = provider.GetRequiredService<MergeQueueController>();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user ?? new ClaimsPrincipal(new ClaimsIdentity()), RequestServices = provider },
        };
        return controller;
    }

    /// <summary>
    /// A signed-in principal whose editable name is an organisation's login, so a controller that read the principal instead
    /// of the verified identity would act as that organisation (T1).
    /// </summary>
    private static ClaimsPrincipal SignedIn()
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "SparkUsers/1"), new Claim(ClaimTypes.Name, "acme")], "Identity.Application"));

    private async Task<IDocumentStore> SeedAsync(IDocumentStore store, bool isPrivate = false, Action<IAsyncDocumentSession>? more = null)
    {
        using (var session = store.OpenAsyncSession())
        {
            await session.StoreAsync(new Repository
            {
                GitHubId = RepoGitHubId, Provider = EForgeProvider.GitHub, Name = "widget", FullName = "acme/widget", OwnerLogin = "acme",
                DefaultBranch = "master", IsPrivate = isPrivate,
            }, RepositoryId);
            more?.Invoke(session);
            await session.SaveChangesAsync();
        }
        WaitForIndexing(store);
        return store;
    }

    private static MergeQueueEntry Entry(int number, EMergeQueueEntryState state, string history)
    {
        var entry = new MergeQueueEntry
        {
            Id = MergeQueueEntry.DocumentId(RepositoryId, number), Repository = RepositoryId, BaseBranch = "master", Number = number, Run = 1,
            State = state, Title = $"Pull request {number}", AuthorLogin = "contributor", EnqueuedBy = "maintainer", EnqueuedVia = EMergeQueueCommandSource.Label,
            EnqueuedAtUtc = new DateTime(2026, 10, 14, 9, 0, 0, DateTimeKind.Utc), StateChangedAtUtc = new DateTime(2026, 10, 14, 9, number, 0, DateTimeKind.Utc),
            Config = new MergeQueueConfigSnapshot
            {
                ConfigRef = "master",
                Values = [new() { Key = "approvalFloor", Value = "ExternalAuthors", Source = "default" }, new() { Key = "postRebaseActionName", Value = "regenerate.yml", Source = "coverage.yml" }],
            },
        };
        entry.Note(entry.StateChangedAtUtc, history);
        return entry;
    }

    private static MergeQueueOutcome Outcome(int number, EMergeQueueEntryState state, string? reason, string branch = "master", int minute = 0) => new()
    {
        Number = number, Title = $"Pull request {number}", BaseBranch = branch, Run = 1, State = state, Reason = reason,
        Message = reason is null ? null : $"because {reason}", EnqueuedBy = "maintainer",
        FinishedAtUtc = new DateTime(2026, 10, 14, 8, minute, 0, DateTimeKind.Utc),
    };

    private static void Busy(IAsyncDocumentSession session)
    {
        var queue = new QueueDocument { Id = QueueDocument.DocumentId(RepositoryId), Repository = RepositoryId };
        var lane = queue.LaneFor("master");
        lane.Current = MergeQueueEntry.DocumentId(RepositoryId, 7);
        lane.Waiting.AddRange([MergeQueueEntry.DocumentId(RepositoryId, 9), MergeQueueEntry.DocumentId(RepositoryId, 8)]);
        queue.Record(Outcome(5, EMergeQueueEntryState.Merged, null, minute: 1));
        queue.Record(Outcome(6, EMergeQueueEntryState.Evicted, "author-push", minute: 2));
        queue.Record(Outcome(10, EMergeQueueEntryState.Refused, "branch", branch: "release", minute: 3));
        session.StoreAsync(queue, queue.Id).GetAwaiter().GetResult();
        foreach (var entry in new[]
        {
            Entry(7, EMergeQueueEntryState.AwaitingChecks, "published 555555555555"),
            Entry(9, EMergeQueueEntryState.Queued, "queued by @maintainer (label)"),
            Entry(8, EMergeQueueEntryState.Queued, "queued by @maintainer (button)"),
        })
            session.StoreAsync(entry, entry.Id).GetAwaiter().GetResult();
    }

    private static string Describe(IConvertToActionResult result) => result.Convert() switch
    {
        JsonResult json => $"{json.StatusCode} {JsonSerializer.Serialize(json.Value, JsonSerializerOptions.Web)}",
        ObjectResult obj => $"{obj.StatusCode} {JsonSerializer.Serialize(obj.Value, JsonSerializerOptions.Web)}",
        StatusCodeResult status => $"{status.StatusCode}",
        var other => other.GetType().Name,
    };

    private static MergeQueueController.MergeQueueView View(ActionResult<MergeQueueController.MergeQueueView> result)
        => (MergeQueueController.MergeQueueView)((OkObjectResult)result.Result!).Value!;

    // ── The panel (B2) ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_panel_shows_each_lane_with_its_current_entry_the_waiting_ones_in_order_and_how_runs_ended()
    {
        using var store = await SeedAsync(GetDocumentStore(), more: Busy);
        using var session = store.OpenAsyncSession();

        var view = View(await Controller(session, new World()).Get("github", "acme", "widget", default));

        view.Supported.Should().BeTrue();
        view.Lanes.Select(l => l.BaseBranch).Should().Equal(["master", "release"], "the default branch first; a refusal for another base still shows");
        var master = view.Lanes[0];
        master.Current!.Number.Should().Be(7);
        master.Current.State.Should().Be("AwaitingChecks");
        master.Current.Step.Should().Be("published 555555555555");
        master.Current.Position.Should().Be(1);
        master.Waiting.Select(w => (w.Number, w.Position)).Should().Equal([(9, 2), (8, 3)], "in queue order, not by number");
        master.Recent.Select(r => (r.Number, r.State, r.Reason)).Should().Equal([(6, "Evicted", "author-push"), (5, "Merged", null)], "newest first");
        view.Lanes[1].Recent.Should().ContainSingle().Which.Reason.Should().Be("branch");

        view.Config!.Number.Should().Be(7, "O15: the configuration the running entry was queued under");
        view.Config.Values.Select(v => (v.Key, v.Source)).Should().Equal([("approvalFloor", "default"), ("postRebaseActionName", "coverage.yml")]);
        view.Viewer.Should().Be(new MergeQueueController.ViewerView(false, false, null, null, null), "an anonymous visitor of a public repository may look, not act");
    }

    [Fact]
    public async Task An_idle_queue_shows_the_configuration_the_latest_run_used()
    {
        using var store = await SeedAsync(GetDocumentStore(), more: session =>
        {
            var queue = new QueueDocument { Id = QueueDocument.DocumentId(RepositoryId), Repository = RepositoryId };
            queue.Record(Outcome(5, EMergeQueueEntryState.Merged, null));
            session.StoreAsync(queue, queue.Id).GetAwaiter().GetResult();
            var ended = Entry(5, EMergeQueueEntryState.Merged, "Merged");
            session.StoreAsync(ended, ended.Id).GetAwaiter().GetResult();
        });
        using var session = store.OpenAsyncSession();

        var view = View(await Controller(session, new World()).Get("github", "acme", "widget", default));

        view.Lanes.Should().ContainSingle().Which.Current.Should().BeNull();
        view.Config!.Number.Should().Be(5);
    }

    [Fact]
    public async Task A_signed_in_viewer_is_told_the_login_the_button_acts_as_and_their_role()
    {
        using var store = await SeedAsync(GetDocumentStore());
        using var session = store.OpenAsyncSession();
        var world = new World { Verified = new GitHubVerifiedLogin("contributor", true, GitHubTokenState.Ok) };
        world.Queue.Roles["contributor"] = EForgeRepositoryRole.Triage;

        var view = View(await Controller(session, world, SignedIn()).Get("github", "acme", "widget", default));

        view.Viewer.Should().Be(new MergeQueueController.ViewerView(true, true, "contributor", "triage", "Ok"));
        world.Queue.RoleQueries.Should().Equal(["contributor"], "the verified login, not the principal's editable name (T1)");
    }

    [Fact]
    public async Task A_forge_without_a_merge_queue_has_no_panel_and_no_button()
    {
        using var store = await SeedAsync(GetDocumentStore());
        using var session = store.OpenAsyncSession();
        var world = new World { QueueSupported = false, Verified = new GitHubVerifiedLogin("contributor", true, GitHubTokenState.Ok) };

        View(await Controller(session, world, SignedIn()).Get("github", "acme", "widget", default)).Supported.Should().BeFalse();
        Describe(await Controller(session, world, SignedIn()).Enqueue("github", "acme", "widget", 7, default))
            .Should().StartWith("409 ");
        world.Bus.Messages.Should().BeEmpty();
    }

    // ── The button (B1, O8, T1) ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_button_raises_the_queues_own_command_as_the_viewers_verified_GitHub_login()
    {
        using var store = await SeedAsync(GetDocumentStore());
        using var session = store.OpenAsyncSession();
        var world = new World { Verified = new GitHubVerifiedLogin("contributor", true, GitHubTokenState.Ok) };
        world.PullRequests[7] = MergeQueueHarness.PullRequest(7);

        var result = await Controller(session, world, SignedIn()).Enqueue("github", "acme", "widget", 7, default);

        Describe(result).Should().Be("""202 {"number":7,"command":"enqueue","actor":"contributor"}""");
        world.Bus.Commands.Should().Equal([new MergeQueueCommandIssued(RepositoryId, 7, EMergeQueueCommand.Enqueue, "contributor", EMergeQueueCommandSource.Button)],
            "the same event a label or a comment raises, so the engine's O9 check is the gate for all three (O8)");
        ((ForgeWebhookMessage<MergeQueueCommandIssued>)world.Bus.Messages[0]).Provider.Should().Be(EForgeProvider.GitHub);
    }

    [Fact]
    public async Task Without_a_linked_GitHub_sign_in_the_button_refuses_with_a_message_and_raises_nothing()
    {
        using var store = await SeedAsync(GetDocumentStore());
        using var session = store.OpenAsyncSession();
        var world = new World();
        world.PullRequests[7] = MergeQueueHarness.PullRequest(7);

        var result = Describe(await Controller(session, world, SignedIn()).Enqueue("github", "acme", "widget", 7, default));

        result.Should().StartWith("403 ").And.Contain("no linked GitHub sign-in");
        world.Bus.Messages.Should().BeEmpty();
    }

    [Theory]
    [InlineData(GitHubTokenState.ReauthRequired, "403 ", "expired")]
    [InlineData(GitHubTokenState.Unavailable, "503 ", "could not confirm")]
    public async Task A_linked_sign_in_GitHub_cannot_confirm_is_refused_with_its_own_reason(GitHubTokenState state, string status, string says)
    {
        using var store = await SeedAsync(GetDocumentStore());
        using var session = store.OpenAsyncSession();
        var world = new World { Verified = new GitHubVerifiedLogin(null, true, state) };
        world.PullRequests[7] = MergeQueueHarness.PullRequest(7);

        var result = Describe(await Controller(session, world, SignedIn()).Enqueue("github", "acme", "widget", 7, default));

        result.Should().StartWith(status).And.Contain(says);
        world.Bus.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task A_number_that_names_no_pull_request_is_refused_before_anything_is_raised()
    {
        using var store = await SeedAsync(GetDocumentStore());
        using var session = store.OpenAsyncSession();
        var world = new World { Verified = new GitHubVerifiedLogin("contributor", true, GitHubTokenState.Ok) };

        Describe(await Controller(session, world, SignedIn()).Enqueue("github", "acme", "widget", 4040, default))
            .Should().Be("""422 {"error":"acme/widget has no pull request #4040."}""");
        Describe(await Controller(session, world, SignedIn()).Enqueue("github", "acme", "widget", 0, default)).Should().StartWith("400 ");
        world.Bus.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task Dequeue_raises_the_dequeue_command_for_an_entry_in_the_queue_and_enqueue_does_not_repeat_one()
    {
        using var store = await SeedAsync(GetDocumentStore(), more: Busy);
        using var session = store.OpenAsyncSession();
        var world = new World { Verified = new GitHubVerifiedLogin("contributor", true, GitHubTokenState.Ok) };
        world.PullRequests[7] = MergeQueueHarness.PullRequest(7);

        Describe(await Controller(session, world, SignedIn()).Enqueue("github", "acme", "widget", 7, default))
            .Should().Be("""409 {"error":"#7 is already in the queue."}""");
        Describe(await Controller(session, world, SignedIn()).Dequeue("github", "acme", "widget", 5, default))
            .Should().Be("""409 {"error":"#5 is not in the queue."}""", "#5 merged: its run is over");
        Describe(await Controller(session, world, SignedIn()).Dequeue("github", "acme", "widget", 8, default))
            .Should().Be("""202 {"number":8,"command":"dequeue","actor":"contributor"}""");

        world.Bus.Commands.Should().Equal([new MergeQueueCommandIssued(RepositoryId, 8, EMergeQueueCommand.Dequeue, "contributor", EMergeQueueCommandSource.Button)],
            "the engine decides whether the actor may (the author, or write)");
    }

    [Fact]
    public async Task A_private_repository_the_viewer_may_see_takes_the_button_like_a_public_one()
    {
        using var store = await SeedAsync(GetDocumentStore(), isPrivate: true);
        using var session = store.OpenAsyncSession();
        var world = new World { Owners = ["acme"], Verified = new GitHubVerifiedLogin("contributor", true, GitHubTokenState.Ok) };
        world.PullRequests[7] = MergeQueueHarness.PullRequest(7);

        Describe(await Controller(session, world, SignedIn()).Enqueue("github", "acme", "widget", 7, default)).Should().StartWith("202 ");
        world.Bus.Commands.Should().ContainSingle();
    }
}
