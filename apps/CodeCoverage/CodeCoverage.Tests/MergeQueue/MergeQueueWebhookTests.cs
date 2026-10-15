using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.MergeQueue;
using CodeCoverage.Recipients;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Webhooks.GitHub.Configuration;
using MintPlayer.Spark.Webhooks.GitHub.Messages;
using Octokit.Webhooks;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.MergeQueue;

/// <summary>
/// The GitHub normaliser's merge-queue events (PRD M5) and the forward onto the saga's lane: who the actor is (T1), which
/// deliveries are the App's own (self-echo, D46), that frequent check deliveries cost nothing while nothing is queued, and
/// that a click on the queue check's button passes both of those filters (§0 O30).
/// </summary>
public class MergeQueueWebhookTests : CoverageRavenTest
{
    private const long AppId = 4242;
    private const string Sha = "3333333333333333333333333333333333333333";
    private static readonly string RepositoryId = Entities.Repository.DocumentId(EForgeProvider.GitHub, MergeQueueHarness.RepoGitHubId);

    private static async Task SeedAsync(IDocumentStore store, bool busy)
    {
        using var session = store.OpenAsyncSession();
        await session.StoreAsync(new Entities.Repository
        {
            GitHubId = MergeQueueHarness.RepoGitHubId, Name = "widget", FullName = "acme/widget", OwnerLogin = "acme", DefaultBranch = "master",
        }, RepositoryId);
        if (busy)
        {
            var queue = new Entities.MergeQueue { Repository = RepositoryId };
            queue.LaneFor("master").Current = MergeQueueEntry.DocumentId(RepositoryId, 7);
            await session.StoreAsync(queue, Entities.MergeQueue.DocumentId(RepositoryId));
            await session.StoreAsync(new MergeQueueEntry { Repository = RepositoryId, Number = 7, State = EMergeQueueEntryState.AwaitingChecks },
                MergeQueueEntry.DocumentId(RepositoryId, 7));
        }
        await session.SaveChangesAsync();
    }

    private static async Task<RecordingMessageBus> DeliverAsync(IDocumentStore store, string eventType, string json)
    {
        var bus = new RecordingMessageBus();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        using var session = store.OpenAsyncSession();
        services.AddSingleton<IAsyncDocumentSession>(session);
        services.AddSingleton<MintPlayer.Spark.Messaging.Abstractions.IMessageBus>(bus);
        services.AddSingleton<MintPlayer.Spark.Webhooks.GitHub.Services.IGitHubInstallationService>(new StubGitHub());
        services.AddSingleton<CodeCoverage.Services.IGitHubIndexQueries, CodeCoverage.Services.GitHubIndexQueries>();
        services.Configure<GitHubWebhooksOptions>(o => o.ProductionAppId = AppId);
        services.AddScoped<GitHubEventsRecipient>();
        var recipient = services.BuildServiceProvider().GetRequiredService<GitHubEventsRecipient>();
        await recipient.HandleAsync(new GitHubWebhookMessage
        {
            Headers = new WebhookHeaders(),
            InstallationId = 1,
            RepositoryFullName = "acme/widget",
            EventType = eventType,
            EventJson = json,
        });
        return bus;
    }

    private static string Repo => $$$"""{"id":{{{MergeQueueHarness.RepoGitHubId}}},"full_name":"acme/widget"}""";

    private static string Labeled(string action, string label, string sender, string senderType = "User") => $$$"""
        {"action":"{{{action}}}","number":7,"pull_request":{"number":7,"merged":false},"label":{"name":"{{{label}}}"},
         "repository":{{{Repo}}},"sender":{"login":"{{{sender}}}","type":"{{{senderType}}}"}}
        """;

    private static string Issue(bool onPullRequest) => onPullRequest
        ? """{"number":7,"pull_request":{"url":"https://api.github.com/repos/acme/widget/pulls/7"}}"""
        : """{"number":7}""";

    private static string Comment(string body, string sender, bool onPullRequest = true, string senderType = "User") => $$$"""
        {"action":"created","issue":{{{Issue(onPullRequest)}}},
         "comment":{"body":{{{System.Text.Json.JsonSerializer.Serialize(body)}}},"user":{"login":"{{{sender}}}"}},
         "repository":{{{Repo}}},"sender":{"login":"{{{sender}}}","type":"{{{senderType}}}"}}
        """;

    [Fact]
    public async Task A_label_is_an_enqueue_command_of_its_sender()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, busy: false);

        var bus = await DeliverAsync(store, "pull_request", Labeled("labeled", "merge-queue", "maintainer"));

        var command = bus.Of<ForgeWebhookMessage<MergeQueueCommandIssued>>().Should().ContainSingle().Which.Event;
        command.Should().Be(new MergeQueueCommandIssued(RepositoryId, 7, EMergeQueueCommand.Enqueue, "maintainer", EMergeQueueCommandSource.Label, "merge-queue"));
    }

    [Fact]
    public async Task An_unlabel_is_a_dequeue_command()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, busy: false);

        var bus = await DeliverAsync(store, "pull_request", Labeled("unlabeled", "merge-queue", "maintainer"));

        bus.Of<ForgeWebhookMessage<MergeQueueCommandIssued>>().Should().ContainSingle().Which.Event.Command.Should().Be(EMergeQueueCommand.Dequeue);
    }

    [Fact]
    public async Task A_bots_label_or_comment_is_never_a_command()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, busy: false);

        // The App's own label removal on eviction arrives exactly like this (D46).
        var label = await DeliverAsync(store, "pull_request", Labeled("unlabeled", "merge-queue", "coveragedevelopment[bot]", "Bot"));
        var comment = await DeliverAsync(store, "issue_comment", Comment("/queue", "dependabot[bot]", senderType: "Bot"));

        label.Messages.Should().BeEmpty();
        comment.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task A_comment_command_acts_for_the_commenter_not_for_whom_it_names()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, busy: false);

        var bus = await DeliverAsync(store, "issue_comment", Comment("/queue as @maintainer\nplease", "outsider"));

        var command = bus.Of<ForgeWebhookMessage<MergeQueueCommandIssued>>().Should().ContainSingle().Which.Event;
        command.ActorLogin.Should().Be("outsider", "T1: the actor is the signed delivery's sender");
        command.Source.Should().Be(EMergeQueueCommandSource.Comment);
        command.Command.Should().Be(EMergeQueueCommand.Enqueue);
    }

    [Theory]
    [InlineData("/dequeue", true, true)]
    [InlineData("  /QUEUE", true, true)]
    [InlineData("please /queue this", true, false)]
    [InlineData("/queued", true, false)]
    [InlineData("/queue", false, false)]
    public async Task Only_a_leading_command_on_a_pull_request_counts(string body, bool onPullRequest, bool raised)
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, busy: false);

        var bus = await DeliverAsync(store, "issue_comment", Comment(body, "maintainer", onPullRequest));

        bus.Of<ForgeWebhookMessage<MergeQueueCommandIssued>>().Should().HaveCount(raised ? 1 : 0);
    }

    [Fact]
    public async Task An_unmerged_close_is_its_own_event()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, busy: false);

        var bus = await DeliverAsync(store, "pull_request", Labeled("closed", "x", "maintainer"));

        bus.Of<ForgeWebhookMessage<PullRequestClosed>>().Should().ContainSingle().Which.Event.Should().Be(new PullRequestClosed(RepositoryId, 7));
    }

    [Fact]
    public async Task Check_deliveries_cost_nothing_while_nothing_is_queued()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, busy: false);

        var bus = await DeliverAsync(store, "check_run", CheckRun("completed", 15368));

        bus.Messages.Should().BeEmpty();
    }

    private static string CheckRun(string action, long appId) => $$$"""
        {"action":"{{{action}}}","check_run":{"head_sha":"{{{Sha}}}","app":{"id":{{{appId}}}}},"repository":{{{Repo}}}}
        """;

    [Fact]
    public async Task While_busy_checks_and_runs_are_raised_but_the_Apps_own_check_run_is_not()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, busy: true);

        var other = await DeliverAsync(store, "check_run", CheckRun("completed", 15368));
        var ours = await DeliverAsync(store, "check_run", CheckRun("created", AppId));
        var status = await DeliverAsync(store, "status", $$$"""{"sha":"{{{Sha}}}","state":"success","context":"ci/x","repository":{{{Repo}}}}""");
        // The dispatched regenerate run's sender is the App itself; that one is the point, never dropped.
        var run = await DeliverAsync(store, "workflow_run", $$$"""
            {"action":"completed","workflow_run":{"id":9001,"head_sha":"{{{Sha}}}","conclusion":"success"},"repository":{{{Repo}}},
             "sender":{"login":"coveragedevelopment[bot]","type":"Bot"}}
            """);
        var review = await DeliverAsync(store, "pull_request_review", $$$"""{"action":"submitted","pull_request":{"number":7},"repository":{{{Repo}}}}""");

        other.Of<ForgeWebhookMessage<CommitChecksChanged>>().Should().ContainSingle().Which.Event.Sha.Should().Be(Sha);
        ours.Messages.Should().BeEmpty("the queue's own check run would wake the queue about itself");
        status.Of<ForgeWebhookMessage<CommitChecksChanged>>().Should().ContainSingle();
        run.Of<ForgeWebhookMessage<CiRunCompleted>>().Should().ContainSingle().Which.Event.Should().Be(new CiRunCompleted(RepositoryId, 9001, Sha, "success"));
        review.Of<ForgeWebhookMessage<PullRequestReviewed>>().Should().ContainSingle();
    }

    // ── The button on the queue's own check (§0 O30) ─────────────────────────────────────────────────

    /// <summary>A click on a check run's button: GitHub's <c>check_run</c> <c>requested_action</c>, sent to the App that made the run.</summary>
    private static string Click(
        string identifier = "mq-enqueue", long appId = AppId, string sender = "maintainer", string senderType = "User",
        string name = "Merge queue", string? externalId = "7", string pullRequests = "[]") => $$$"""
        {"action":"requested_action","requested_action":{"identifier":"{{{identifier}}}"},
         "check_run":{"id":42,"name":"{{{name}}}","head_sha":"{{{Sha}}}","status":"completed","conclusion":"neutral",
          "external_id":{{{(externalId is null ? "null" : $"\"{externalId}\"")}}},"app":{"id":{{{appId}}},"slug":"coveragedevelopment"},
          "pull_requests":{{{pullRequests}}}},
         "repository":{{{Repo}}},"sender":{"login":"{{{sender}}}","type":"{{{senderType}}}"}}
        """;

    /// <summary>
    /// The click comes from the App's own check run, which the self-echo filter drops for every other check delivery, and
    /// the busy filter drops check deliveries while nothing is queued: both must let it through, for "Add to merge queue"
    /// (§0 O30) and for "Remove from queue" (§0 O32) alike. A fork's check run lists no pull request on GitHub, so the run's
    /// <c>external_id</c>, which the queue writes, names it.
    /// </summary>
    [Theory]
    [InlineData("mq-enqueue", EMergeQueueCommand.Enqueue, false)]
    [InlineData("mq-enqueue", EMergeQueueCommand.Enqueue, true)]
    [InlineData("mq-dequeue", EMergeQueueCommand.Dequeue, false)]
    [InlineData("mq-dequeue", EMergeQueueCommand.Dequeue, true)]
    public async Task A_click_on_a_queue_check_button_is_the_clickers_command_whatever_is_queued(string identifier, EMergeQueueCommand command, bool busy)
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, busy);

        var bus = await DeliverAsync(store, "check_run", Click(identifier));

        bus.Of<ForgeWebhookMessage<MergeQueueCommandIssued>>().Should().ContainSingle().Which.Event
            .Should().Be(new MergeQueueCommandIssued(RepositoryId, 7, command, "maintainer", EMergeQueueCommandSource.Check));
        bus.Messages.Should().HaveCount(1, "a click is a command, not a change of the commit's checks");
    }

    [Fact]
    public async Task A_click_on_a_check_without_an_external_id_names_the_one_pull_request_GitHub_lists()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, busy: false);

        var bus = await DeliverAsync(store, "check_run", Click(externalId: null,
            pullRequests: $$$"""[{"url":"https://api.github.com/repos/acme/widget/pulls/7","id":1,"number":7,"head":{"ref":"feature","sha":"{{{Sha}}}"}}]"""));

        bus.Of<ForgeWebhookMessage<MergeQueueCommandIssued>>().Should().ContainSingle().Which.Event.Number.Should().Be(7);
    }

    [Theory]
    [InlineData("another button", "mq-other", AppId, "maintainer", "User", "Merge queue", "7")]
    [InlineData("another App's check", "mq-enqueue", 15368L, "maintainer", "User", "Merge queue", "7")]
    [InlineData("a bot's click", "mq-enqueue", AppId, "renovate[bot]", "Bot", "Merge queue", "7")]
    [InlineData("another check of ours", "mq-enqueue", AppId, "maintainer", "User", "coverage/project", "7")]
    [InlineData("no pull request", "mq-enqueue", AppId, "maintainer", "User", "Merge queue", null)]
    public async Task A_click_that_is_not_the_queues_enqueue_raises_nothing(string what, string identifier, long appId, string sender, string senderType, string name, string? externalId)
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, busy: true);

        var bus = await DeliverAsync(store, "check_run", Click(identifier, appId, sender, senderType, name, externalId));

        bus.Messages.Should().BeEmpty(what);
    }

    [Fact]
    public async Task An_unknown_repository_raises_nothing()
    {
        using var store = GetDocumentStore();

        var bus = await DeliverAsync(store, "pull_request", Labeled("labeled", "merge-queue", "maintainer"));

        bus.Messages.Should().BeEmpty();
    }

    // ── The forward onto the saga's lane ─────────────────────────────────────────────────────────────

    private static async Task<RecordingMessageBus> ForwardAsync<TEvent>(IDocumentStore store, TEvent evt) where TEvent : class, IForgeEvent
    {
        var bus = new RecordingMessageBus();
        using var session = store.OpenAsyncSession();
        var recipient = new MergeQueueEventsRecipient(session, bus);
        await ((MintPlayer.Spark.Messaging.Abstractions.IRecipient<ForgeWebhookMessage<TEvent>>)recipient).HandleAsync(new ForgeWebhookMessage<TEvent>(EForgeProvider.GitHub, evt));
        return bus;
    }

    /// <remarks>
    /// Any other pull request's update is an offer (§0 O30): whether the repository has a queue at all is only known after
    /// reading <c>coverage.yml</c> on the default branch, which the saga does.
    /// </remarks>
    [Fact]
    public async Task A_queued_pull_requests_update_is_a_head_push_any_other_an_offer_and_a_base_counts_only_for_a_busy_lane()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, busy: true);

        var queued = await ForwardAsync(store, new PullRequestUpdated(RepositoryId, 7, Sha, "feature", "master", "b", null, false, false));
        var other = await ForwardAsync(store, new PullRequestUpdated(RepositoryId, 8, Sha, "feature", "master", "b", null, false, false));
        var baseMoved = await ForwardAsync(store, new BranchCommitPushed(RepositoryId, "master", Sha, null, null));
        var queueBranch = await ForwardAsync(store, new BranchCommitPushed(RepositoryId, "mq/master/pr-7", Sha, null, null));
        var otherBranch = await ForwardAsync(store, new BranchCommitPushed(RepositoryId, "develop", Sha, null, null));

        queued.Of<MergeQueueSignalMessage>().Should().ContainSingle().Which.Kind.Should().Be(EMergeQueueSignal.HeadPushed);
        var offer = other.Of<MergeQueueSignalMessage>().Should().ContainSingle().Which;
        offer.Kind.Should().Be(EMergeQueueSignal.Offer);
        offer.Number.Should().Be(8);
        offer.Sha.Should().Be(Sha);
        baseMoved.Of<MergeQueueSignalMessage>().Should().ContainSingle().Which.Branch.Should().Be("master");
        queueBranch.Messages.Should().BeEmpty();
        otherBranch.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task A_command_is_always_forwarded_with_its_actor()
    {
        using var store = GetDocumentStore();

        var bus = await ForwardAsync(store, new MergeQueueCommandIssued(RepositoryId, 7, EMergeQueueCommand.Enqueue, "maintainer", EMergeQueueCommandSource.Button));

        var signal = bus.Of<MergeQueueSignalMessage>().Should().ContainSingle().Which;
        signal.Kind.Should().Be(EMergeQueueSignal.Command);
        signal.Actor.Should().Be("maintainer");
        signal.Source.Should().Be(EMergeQueueCommandSource.Button);
    }
}
