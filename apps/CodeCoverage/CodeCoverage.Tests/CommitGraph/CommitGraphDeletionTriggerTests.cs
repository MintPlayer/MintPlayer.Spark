using CodeCoverage.CommitGraph;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Ingestion;
using CodeCoverage.Recipients;
using CodeCoverage.Tests.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Messaging.Abstractions;
using NSubstitute;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.CommitGraph;

/// <summary>
/// PRD §5.2, "the mirror dies with the access": each forge-neutral deletion trigger reaches the
/// sidecar's DELETE and removes the state. The GitHub-specific triggers (uninstall, removal from an
/// installation) run through the same neutral handler and are covered in
/// <c>GitHubRepositoryLifecycleTests</c>.
/// </summary>
public class CommitGraphDeletionTriggerTests : CoverageRavenTest
{
    protected override bool DeployIndexes => true;

    private const long RepoGitHubId = 77;
    private static readonly string RepoId = Repository.DocumentId(EForgeProvider.GitHub, RepoGitHubId);

    private static async Task<CommitGraphKit> SeedMirroredAsync(IDocumentStore store, RepositoryConnection connection = RepositoryConnection.Connected)
    {
        var kit = new CommitGraphKit(store);
        using (var session = store.OpenAsyncSession())
        {
            await session.StoreAsync(new Account { GitHubId = 1, Login = "acme", Type = "Organization" }, Account.DocumentId(EForgeProvider.GitHub, 1));
            var repository = new Repository
            {
                GitHubId = RepoGitHubId, Provider = EForgeProvider.GitHub, Account = Account.DocumentId(EForgeProvider.GitHub, 1),
                Name = "widgets", FullName = "acme/widgets", OwnerLogin = "acme",
            };
            if (connection == RepositoryConnection.Disconnected) repository.MarkDisconnected(DisconnectedReasons.RemovedFromInstallation);
            await session.StoreAsync(repository, RepoId);
            await session.SaveChangesAsync();
        }
        await CommitGraphKit.SeedMirrorAsync(store, kit.Sidecar, RepoGitHubId, 10, DateTime.UtcNow);
        return kit;
    }

    private static async Task AssertMirrorGoneAsync(IDocumentStore store, CommitGraphKit kit, RecordingMessageBus bus)
    {
        // Run the queued deletion as the messaging host would.
        foreach (var message in bus.Of<DeleteCommitGraphMirrorMessage>().ToList())
            await new DeleteCommitGraphMirrorRecipient(kit.Get<ICommitGraphMirrors>()).HandleAsync(message);

        kit.Sidecar.Deletes.Should().Equal($"github/{RepoGitHubId}");
        kit.Sidecar.Mirrors.Should().BeEmpty();
        using var session = store.OpenAsyncSession();
        (await session.LoadAsync<RepositoryGraphState>(RepositoryGraphState.ForRepository(RepoId))).Should().BeNull();
    }

    [Fact]
    public async Task A_disconnect_deletes_the_mirror()
    {
        using var store = GetDocumentStore();
        var kit = await SeedMirroredAsync(store);
        var bus = new RecordingMessageBus();
        using var session = store.OpenAsyncSession();

        var services = new ServiceCollection();
        services.AddLogging(l => l.SetMinimumLevel(LogLevel.None));
        services.AddSingleton(session);
        services.AddSingleton<IMessageBus>(bus);
        var forge = new ScriptedDiffService();
        services.AddSingleton<IForgeIntegration>(forge);
        services.AddSingleton<IForgeIntegrationResolver>(forge);
        services.AddScoped<ForgeEventsRecipient>();
        await services.BuildServiceProvider().GetRequiredService<ForgeEventsRecipient>().HandleAsync(
            new ForgeWebhookMessage<RepositoryConnectionChanged>(EForgeProvider.GitHub,
                new RepositoryConnectionChanged(RepoId, Connected: false, Reason: DisconnectedReasons.RemovedFromInstallation)));
        await session.SaveChangesAsync();

        await AssertMirrorGoneAsync(store, kit, bus);
    }

    [Fact]
    public async Task DeleteData_deletes_the_mirror()
    {
        using var store = GetDocumentStore();
        var kit = await SeedMirroredAsync(store, RepositoryConnection.Disconnected);
        var bus = new RecordingMessageBus();
        using var session = store.OpenAsyncSession();

        var services = new ServiceCollection();
        services.AddLogging(l => l.SetMinimumLevel(LogLevel.None));
        services.AddSingleton(session);
        services.AddSingleton<IMessageBus>(bus);
        services.AddScoped<DeleteRepositoryDataRecipient>();
        await services.BuildServiceProvider().GetRequiredService<DeleteRepositoryDataRecipient>()
            .HandleAsync(new DeleteRepositoryDataMessage { RepositoryGitHubId = RepoGitHubId, RequestedByUserId = "users/1" });

        await AssertMirrorGoneAsync(store, kit, bus);
    }

    [Fact]
    public async Task The_nightly_reconcile_finding_no_access_deletes_the_mirror()
    {
        using var store = GetDocumentStore();
        var kit = await SeedMirroredAsync(store);
        WaitForIndexing(store);
        var bus = new RecordingMessageBus();
        using var session = store.OpenAsyncSession();

        // The forge's reconcile finds the repository gone from the installation, as GitHub's does.
        var forge = Substitute.For<IForgeIntegration>();
        forge.Provider.Returns(EForgeProvider.GitHub);
        forge.ReconcileAsync(default!, default).ReturnsForAnyArgs(async _ =>
            (await session.LoadAsync<Repository>(RepoId))!.MarkDisconnected(DisconnectedReasons.IntegrationRemoved));

        await new ReconcileForgeStateCronJob(session, new SingleForgeResolver(forge),
            Substitute.For<CodeCoverage.Dependencies.IManifestScanScheduler>(), NullLogger<ReconcileForgeStateCronJob>.Instance, bus)
            .RunAsync(default);

        bus.Of<DeleteCommitGraphMirrorMessage>().Should().ContainSingle().Which.RepositoryId.Should().Be(RepoId);
        await AssertMirrorGoneAsync(store, kit, bus);
    }

    [Fact]
    public async Task A_reconcile_that_keeps_access_deletes_nothing()
    {
        using var store = GetDocumentStore();
        var kit = await SeedMirroredAsync(store);
        WaitForIndexing(store);
        var bus = new RecordingMessageBus();
        using var session = store.OpenAsyncSession();
        var forge = Substitute.For<IForgeIntegration>();
        forge.Provider.Returns(EForgeProvider.GitHub);

        await new ReconcileForgeStateCronJob(session, new SingleForgeResolver(forge),
            Substitute.For<CodeCoverage.Dependencies.IManifestScanScheduler>(), NullLogger<ReconcileForgeStateCronJob>.Instance, bus)
            .RunAsync(default);

        bus.Of<DeleteCommitGraphMirrorMessage>().Should().BeEmpty();
        kit.Sidecar.Mirrors.Should().ContainSingle();
    }

    [Fact]
    public async Task The_daily_eviction_also_removes_a_mirror_whose_repository_lost_access_without_the_queue_hearing_of_it()
    {
        using var store = GetDocumentStore();
        var kit = await SeedMirroredAsync(store, RepositoryConnection.Disconnected);

        await kit.Get<ICommitGraphMirrors>().EvictIdleAsync();

        kit.Sidecar.Deletes.Should().Equal($"github/{RepoGitHubId}");
        using var session = store.OpenAsyncSession();
        // Lost access is not an eviction: the counter is for space and idleness only.
        (await session.LoadAsync<CommitGraphMetrics>(CommitGraphMetrics.DocumentId)).Should().BeNull();
    }
}
