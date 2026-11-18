using System.Security.Claims;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Ingestion;
using CodeCoverage.Services;
using CodeCoverage.Webhooks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Apps;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using NSubstitute;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.Webhooks;

/// <summary>
/// #691 M9 (<c>docs/apps_webhooks_PRD.md</c> D34): a finalized build is announced as <c>coverage.build.finalized</c> to
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
            Arg.Any<IAsyncDocumentSession>(),
            Arg.Is<CoverageBuildFinalizedWebhookMessage>(m => m.BuildId == BuildId),
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

    private static CoverageBuildFinalizedRecipient Recipient(IAsyncDocumentSession session, IWebhookPublisher? publisher)
    {
        var services = new ServiceCollection();
        if (publisher is not null)
            services.AddSingleton(publisher);
        return new CoverageBuildFinalizedRecipient(session, services.BuildServiceProvider());
    }

    [Fact]
    public async Task The_recipient_publishes_the_build_to_its_account_keyed_by_the_build()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, "Finalized");
        var publisher = Substitute.For<IWebhookPublisher>();
        CoverageBuildFinalized? sent = null;
        publisher.PublishAsync(Arg.Do<CoverageBuildFinalized>(e => sent = e), Arg.Any<WebhookOwner>(), Arg.Any<string>(), Arg.Any<IAsyncDocumentSession>(), Arg.Any<CancellationToken>())
            .Returns("msg_x");

        using (var session = store.OpenAsyncSession())
            await Recipient(session, publisher).HandleAsync(new CoverageBuildFinalizedWebhookMessage { BuildId = BuildId });

        await publisher.Received(1).PublishAsync(
            Arg.Any<CoverageBuildFinalized>(),
            CoverageWebhooks.Account(AcmeKey),
            BuildId,
            Arg.Any<IAsyncDocumentSession>(),
            Arg.Any<CancellationToken>());
        sent.Should().Be(new CoverageBuildFinalized(
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
            await Recipient(session, webhooksOn ? publisher : null).HandleAsync(new CoverageBuildFinalizedWebhookMessage { BuildId = BuildId });

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
}
