using CodeCoverage.Entities;
using CodeCoverage.Recipients;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Webhooks.GitHub.Configuration;
using MintPlayer.Spark.Webhooks.GitHub.Messages;
using Octokit.Webhooks;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.Recipients;

/// <summary>
/// The router's decision of which deliveries reach the board-automation lane at all.
/// </summary>
/// <remarks>
/// <see cref="ProjectAutomationTests"/> pins <c>IsSelfAuthored</c> on its own, with a null
/// session; this runs <c>HandleAsync</c> end to end against RavenDB, so the board lookup and the
/// choice between a keyed and an unkeyed broadcast are asserted too. Every refusal is silent in
/// production — a rule simply never fires — which is why each gets a case.
/// </remarks>
public class ProjectAutomationRouterTests : CoverageRavenTest
{
    protected override bool DeployIndexes => true;

    private const long OurAppId = 4567511;

    private const string OurCheckRun = """{"check_run":{"app":{"id":4567511}}}""";

    private static async Task SeedBoardAsync(IDocumentStore store, string owner, bool enabled = true,
        RepositoryConnection connection = RepositoryConnection.Connected)
    {
        using var seed = store.OpenAsyncSession();
        await seed.StoreAsync(new GitHubProject
        {
            OwnerLogin = owner, NodeId = $"PVT_{owner}", Number = 1, Name = "Board",
            AutomationEnabled = enabled, Connection = connection,
        }, GitHubProject.DocumentId($"PVT_{owner}"));
        await seed.SaveChangesAsync();
    }

    private static GitHubWebhookMessage Delivery(
        string eventType = "pull_request", string json = """{"action":"opened"}""",
        string? repository = "acme/widget", string? delivery = "d-1")
        => new()
        {
            EventType = eventType,
            EventJson = json,
            RepositoryFullName = repository!,
            InstallationId = 7,
            Headers = new WebhookHeaders { Event = eventType, Delivery = delivery },
        };

    private async Task<RecordingMessageBus> RouteAsync(IDocumentStore store, GitHubWebhookMessage message, long? appId = OurAppId)
    {
        WaitForIndexing(store);
        var bus = new RecordingMessageBus();
        using var session = store.OpenAsyncSession();
        var router = new ProjectAutomationRouter(
            session, bus, NullLogger<ProjectAutomationRouter>.Instance,
            Options.Create(new GitHubWebhooksOptions { ProductionAppId = appId }));
        await router.HandleAsync(message);
        return bus;
    }

    [Fact]
    public async Task An_automatable_delivery_for_an_automated_owner_is_forwarded_once_per_delivery()
    {
        using var store = GetDocumentStore();
        await SeedBoardAsync(store, "acme");

        var bus = await RouteAsync(store, Delivery());

        var forwarded = bus.Of<ProjectAutomationMessage>().Should().ContainSingle().Which;
        forwarded.EventType.Should().Be("pull_request");
        forwarded.RepositoryFullName.Should().Be("acme/widget");
        forwarded.InstallationId.Should().Be(7);
        forwarded.DeliveryId.Should().Be("d-1");
        bus.DeduplicationKeys.Should().Equal("automation-d-1");
    }

    /// <summary>
    /// No delivery id: an unkeyed broadcast. Keying on a missing id would give every such delivery
    /// the same key, and all but the first would be discarded as duplicates.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public async Task A_delivery_without_an_id_is_forwarded_without_deduplication(string? delivery)
    {
        using var store = GetDocumentStore();
        await SeedBoardAsync(store, "acme");

        var bus = await RouteAsync(store, Delivery(delivery: delivery));

        bus.Of<ProjectAutomationMessage>().Should().ContainSingle();
        bus.DeduplicationKeys.Should().BeEmpty();
    }

    [Theory]
    [InlineData("push")]
    [InlineData("installation")]
    public async Task An_event_no_rule_can_react_to_is_dropped(string eventType)
    {
        using var store = GetDocumentStore();
        await SeedBoardAsync(store, "acme");

        (await RouteAsync(store, Delivery(eventType: eventType))).Messages.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task A_delivery_without_a_repository_is_dropped(string? repository)
    {
        using var store = GetDocumentStore();
        await SeedBoardAsync(store, "acme");

        (await RouteAsync(store, Delivery(repository: repository))).Messages.Should().BeEmpty();
    }

    /// <summary>The loop guard: our own check run must not move a card, or feedback would chase itself.</summary>
    [Fact]
    public async Task A_delivery_this_app_caused_is_dropped()
    {
        using var store = GetDocumentStore();
        await SeedBoardAsync(store, "acme");

        var bus = await RouteAsync(store, Delivery(eventType: "check_run", json: OurCheckRun));

        bus.Messages.Should().BeEmpty();
    }

    /// <summary>
    /// Without an App id the guard cannot identify our own writes; it lets the delivery through and
    /// warns, rather than dropping every delivery.
    /// </summary>
    [Fact]
    public async Task Without_an_app_id_nothing_is_treated_as_self_authored()
    {
        using var store = GetDocumentStore();
        await SeedBoardAsync(store, "acme");

        var bus = await RouteAsync(store, Delivery(eventType: "check_run", json: OurCheckRun), appId: null);

        bus.Of<ProjectAutomationMessage>().Should().ContainSingle();
    }

    /// <summary>An unparseable body is a shape we do not know; the automation recipient dead-letters it with a reason.</summary>
    [Fact]
    public async Task An_unparseable_body_is_forwarded_rather_than_dropped()
    {
        using var store = GetDocumentStore();
        await SeedBoardAsync(store, "acme");

        (await RouteAsync(store, Delivery(json: "{not json"))).Of<ProjectAutomationMessage>().Should().ContainSingle();
    }

    [Theory]
    [InlineData("other", true, RepositoryConnection.Connected)]         // a board, but another owner's
    [InlineData("acme", false, RepositoryConnection.Connected)]         // automation switched off
    [InlineData("acme", true, RepositoryConnection.Disconnected)]       // no token may reach it any more
    public async Task A_delivery_no_reachable_board_automates_is_dropped(string boardOwner, bool enabled, RepositoryConnection connection)
    {
        using var store = GetDocumentStore();
        await SeedBoardAsync(store, boardOwner, enabled, connection);

        (await RouteAsync(store, Delivery())).Messages.Should().BeEmpty();
    }
}
