using System.Security.Claims;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Ingestion;
using CodeCoverage.Services;
using CodeCoverage.Webhooks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using MintPlayer.Spark.Apps;
using MintPlayer.Spark.Apps.Catalog;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using NSubstitute;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.Webhooks;

/// <summary>
/// #691 M9 (<c>docs/apps_webhooks_PRD.md</c> D34): a finalized build is announced as <c>coverage.build.completed</c> to
/// the webhooks of its repository's forge account. The finalize only queues a message in its own session; the recipient
/// publishes it with the build id as the idempotency key, and webhooks stay off unless the setting turns them on.
/// </summary>
public class CoverageWebhooksTests : CoverageRavenTest
{
    private const long RepoId = 69101;
    private const string Sha = "c0ffee";

    private static readonly string RepositoryId = Repository.DocumentId(EForgeProvider.GitHub, RepoId);
    private static readonly string CommitId = Commit.DocumentId(EForgeProvider.GitHub, RepoId, Sha);
    private static readonly string BuildId = Build.DocumentId(EForgeProvider.GitHub, RepoId, Sha, 7, 2);
    private static readonly string AcmeKey = new ForgeOwner(EForgeProvider.GitHub, "acme").ToString();

    private static async Task SeedAsync(IDocumentStore store, string status)
    {
        using var seed = store.OpenAsyncSession();
        await seed.StoreAsync(new Repository { GitHubId = RepoId, Name = "widgets", FullName = "acme/widgets", OwnerLogin = "acme" }, RepositoryId);
        await seed.StoreAsync(new Commit { Repository = RepositoryId, Sha = Sha, Branch = "feature", PullRequestNumber = 12 }, CommitId);
        await seed.StoreAsync(new Build
        {
            Commit = CommitId,
            CiRunId = 7,
            CiRunAttempt = 2,
            Run = Build.ComposeRun(7, 2),
            WorkflowName = "CI",
            Status = status,
            FinalizeReason = status == "Finalized" ? "Debounce" : null,
            FinalizedAtUtc = status == "Finalized" ? new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc) : null,
            CreatedAtUtc = DateTime.UtcNow,
            LastUploadAtUtc = DateTime.UtcNow,
            Sessions = [new BuildSession { SessionId = "s", ParseStatus = "Parsed" }],
            Coverage = new CoverageSummary { LinesCovered = 80, LinesCoverable = 100, BranchesCovered = 3, BranchesTotal = 4, FilesCount = 5 },
            Patch = new PatchCoverage { LinesCovered = 9, LinesCoverable = 10, DiffBaseRef = "main" },
        }, BuildId);
        await seed.SaveChangesAsync();
    }

    /// <summary>A queue over a container that has the outbox, and the publisher only when webhooks are on.</summary>
    private static (CoverageWebhookQueue Queue, IMessageOutbox Outbox) Queue(bool webhooksOn)
    {
        var services = new ServiceCollection();
        var outbox = Substitute.For<IMessageOutbox>();
        services.AddSingleton(outbox);
        if (webhooksOn)
            services.AddSingleton(Substitute.For<IWebhookPublisher>());
        return (new CoverageWebhookQueue(services.BuildServiceProvider()), outbox);
    }

    private static async Task FinalizeAsync(IDocumentStore store, CoverageWebhookQueue queue)
    {
        var forge = Substitute.For<IForgeIntegration>();
        forge.Provider.Returns(EForgeProvider.GitHub);
        using var session = store.OpenAsyncSession();
        await new FinalizeBuildRecipient(session, new SingleForgeResolver(forge), NullLogger<FinalizeBuildRecipient>.Instance, new RecordingMessageBus(), queue)
            .HandleAsync(new FinalizeBuildMessage { BuildId = BuildId });
    }

    [Fact]
    public async Task A_finalize_queues_the_webhook_in_its_own_session_when_webhooks_are_on()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, "Open");
        var (queue, outbox) = Queue(webhooksOn: true);

        await FinalizeAsync(store, queue);

        await outbox.Received(1).EnqueueAsync(
            Arg.Is<CoverageBuildCompletedWebhookMessage>(m => string.Equals(m.BuildId, BuildId, StringComparison.OrdinalIgnoreCase)),
            Arg.Any<BroadcastOptions?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task With_webhooks_off_a_finalize_queues_nothing()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, "Open");
        var (queue, outbox) = Queue(webhooksOn: false);

        await FinalizeAsync(store, queue);

        outbox.ReceivedCalls().Should().BeEmpty("webhooks are off unless CodeCoverage:Webhooks:Enabled turns them on");
    }

    private static CoverageBuildCompletedRecipient Recipient(IAsyncDocumentSession session, IWebhookPublisher? publisher)
    {
        var services = new ServiceCollection();
        if (publisher is not null)
            services.AddSingleton(publisher);
        return new CoverageBuildCompletedRecipient(session, services.BuildServiceProvider());
    }

    [Fact]
    public async Task The_recipient_publishes_the_build_to_its_account_keyed_by_the_build()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, "Finalized");
        var publisher = Substitute.For<IWebhookPublisher>();
        CoverageBuildCompleted? sent = null;
        publisher.PublishAsync(Arg.Do<CoverageBuildCompleted>(e => sent = e), Arg.Any<WebhookOwner>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("msg_x");

        using (var session = store.OpenAsyncSession())
            await Recipient(session, publisher).HandleAsync(new CoverageBuildCompletedWebhookMessage { BuildId = BuildId });

        await publisher.Received(1).PublishAsync(
            Arg.Any<CoverageBuildCompleted>(),
            CoverageWebhooks.Account(AcmeKey),
            // The delivery key is the loaded build's id, lower case (#536 D7).
            BuildId.ToLowerInvariant(),
            Arg.Any<CancellationToken>());
        sent.Should().Be(new CoverageBuildCompleted(
            "acme/widgets", "github", Sha, "feature", 12, Build.ComposeRun(7, 2), "CI", "Complete", "Debounce", false,
            new CoverageNumbers(80, 100, 3, 4, 5),
            new PatchNumbers(9, 10, "main"),
            new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero)),
            "the event describes the finalized build, its commit and its repository");
    }

    [Theory]
    [InlineData("Open", true)]       // reopened or not yet saved: nothing final to announce
    [InlineData("Finalized", false)] // webhooks switched off after the finalize queued the message
    public async Task The_recipient_publishes_nothing_for_an_open_build_or_with_webhooks_off(string status, bool webhooksOn)
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, status);
        var publisher = Substitute.For<IWebhookPublisher>();

        using (var session = store.OpenAsyncSession())
            await Recipient(session, webhooksOn ? publisher : null).HandleAsync(new CoverageBuildCompletedWebhookMessage { BuildId = BuildId });

        publisher.ReceivedCalls().Should().BeEmpty("there is no finalized build to announce, or no publisher to announce it with");
    }

    private static AccountWebhookOwnerProvider Owners(ISparkVisibility visibility, string? signedInUser)
    {
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        if (signedInUser is not null)
            http.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, signedInUser)], "test"));
        return new AccountWebhookOwnerProvider(visibility, http);
    }

    [Fact]
    public async Task An_account_is_managed_by_whoever_the_forge_says_manages_it()
    {
        var visibility = Substitute.For<ISparkVisibility>();
        visibility.CanManageOwnerAsync(AcmeKey).Returns(true);
        visibility.GetManagedOwnersAsync().Returns([AcmeKey, "not-an-owner-key"]);
        var owners = Owners(visibility, "users/ann");

        (await owners.CanManageAsync(AcmeKey, "users/ann", default)).Should().BeTrue("the forge says ann manages acme");
        (await owners.ListManageableAsync("users/ann", default)).Should().Equal(
            [new WebhookOwnerDescription(CoverageWebhooks.AccountKind, AcmeKey, "acme (GitHub)")],
            "only well-formed forge owners are listed");
    }

    [Fact]
    public async Task The_owner_provider_answers_only_for_the_signed_in_user_and_well_formed_keys()
    {
        var visibility = Substitute.For<ISparkVisibility>();
        visibility.CanManageOwnerAsync(Arg.Any<string>()).Returns(true);
        visibility.GetManagedOwnersAsync().Returns([AcmeKey]);

        // Visibility answers for the request's user: a question about someone else is refused, not answered for ann.
        (await Owners(visibility, "users/ann").CanManageAsync(AcmeKey, "users/bob", default)).Should().BeFalse("bob is not the signed-in user");
        (await Owners(visibility, "users/ann").ListManageableAsync("users/bob", default)).Should().BeEmpty("bob is not the signed-in user");
        (await Owners(visibility, null).CanManageAsync(AcmeKey, "users/ann", default)).Should().BeFalse("nobody is signed in");
        (await Owners(visibility, "users/ann").CanManageAsync("acme", "users/ann", default)).Should().BeFalse("acme is not a provider:login key");
        await visibility.DidNotReceive().CanManageOwnerAsync("acme");
    }

    /// <summary>
    /// #691 D41: the event names and permissions are the receivers' contract, and every payload carries exactly the fields
    /// its published schema lists (a receiver generates its types from that schema).
    /// </summary>
    [Fact]
    public void With_webhooks_on_the_catalog_lists_the_ten_events_and_each_payload_matches_its_schema()
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [CoverageWebhooks.EnabledSetting] = "true" }).Build();
        var services = new ServiceCollection();
        new MintPlayer.Spark.SparkBuilder(services, configuration).AddCoverageWebhooks(configuration);
        var catalog = services.BuildServiceProvider().GetRequiredService<WebhookEventCatalog>();

        catalog.Events.Select(e => (e.Name, e.Permission)).Should().BeEquivalentTo(new[]
        {
            ("coverage.report.processed", "coverage:read"), ("coverage.build.completed", "coverage:read"),
            ("merge_queue.entry.queued", "merge_queue:read"), ("merge_queue.entry.merged", "merge_queue:read"), ("merge_queue.entry.removed", "merge_queue:read"),
            ("dependencies.pull_request.opened", "dependencies:read"), ("dependencies.run.completed", "dependencies:read"),
            ("versioning.run.completed", "versioning:read"), ("backport.attempted", "versioning:read"), ("backport.merged", "versioning:read"),
        });

        var at = new DateTimeOffset(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);
        var pull = new CodeCoverage.MergeQueue.MergeQueuePullRequest("acme/widgets", "github", 7, "fix: guard", "main", 1, "ann");
        var run = new CodeCoverage.Dependencies.DependencyRun("acme/widgets", "github", null, "main", 3, "Schedule");
        object[] samples =
        [
            new CoverageReportProcessed("acme/widgets", "github", Sha, "main", 12, "7.2", "CI", "s1", "test", ["unit"], "Parsed", null, 3,
                [new CoverageReportFile("lcov.info", true, "LCOV", 3, null, null)], at),
            new CoverageBuildCompleted("acme/widgets", "github", Sha, "main", 12, "7.2", "CI", "Complete", "Explicit", false,
                new CoverageNumbers(80, 100, 3, 4, 5), new PatchNumbers(9, 10, "main"), at),
            new CodeCoverage.MergeQueue.MergeQueueEntryQueued(pull, at),
            new CodeCoverage.MergeQueue.MergeQueueEntryMerged(pull, Sha, at),
            new CodeCoverage.MergeQueue.MergeQueueEntryRemoved(pull, "evicted", "checks-failed", "CI failed", at),
            new CodeCoverage.Dependencies.DependencyPullRequestOpened(run, 8, "Update dependencies", "dependencies/rolling", false, at),
            new CodeCoverage.Dependencies.DependencyRunCompleted(run, "published", "Published #8", 8, at),
            new CodeCoverage.PackageVersioning.VersioningRunResult("acme/widgets", "github", "main", Sha, "77.2", "versioned", null,
                [new CodeCoverage.PackageVersioning.VersioningPackage("Acme.Widget", "nuget", "1.2.0", "Acme.Widget/v1.2.0")], ["Acme.Other"], at),
            new CodeCoverage.PackageVersioning.BackportAttempted("acme/widgets", "github", 7, Sha, "release/1.x", "opened", 9, null, at),
            new CodeCoverage.PackageVersioning.BackportMerged("acme/widgets", "github", 9, 7, "release/1.x", at),
        ];
        samples.Select(s => catalog.Find(s.GetType())?.Name).Should().OnlyHaveUniqueItems().And.NotContainNulls("every sample is a registered event");

        foreach (var sample in samples)
        {
            var schema = catalog.Find(sample.GetType())!.Schema;
            var payload = System.Text.Json.JsonSerializer.SerializeToNode(sample, sample.GetType(), MintPlayer.Webhooks.WebhookJson.Options)!.AsObject();
            payload.Select(p => p.Key).Order().Should().Equal(schema["properties"]!.AsObject().Select(p => p.Key).Order(), sample.GetType().Name);
        }
    }
}
