using CodeCoverage.Dependencies;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Ingestion;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.Dependencies;

/// <summary>
/// The scheduled manifest-scan triggers: after an account reconcile (nightly, and the one an
/// installation change queues) every connected, unarchived repository of the account is queued once
/// per day.
/// </summary>
public class ManifestScanSchedulingTests : CoverageRavenTest
{
    protected override bool DeployIndexes => true;

    private static string RepoId(long id) => Repository.DocumentId(EForgeProvider.GitHub, id);

    private static async Task<Account> SeedAsync(IDocumentStore store)
    {
        var account = new Account { GitHubId = 77, Login = "acme", InstallationId = 5 };
        using var seed = store.OpenAsyncSession();
        await seed.StoreAsync(account, Account.DocumentId(EForgeProvider.GitHub, 77));

        Repository Repo(long id, string owner, string name) => new()
        {
            GitHubId = id, Name = name, FullName = $"{owner}/{name}", OwnerLogin = owner, DefaultBranch = "main",
        };

        await seed.StoreAsync(Repo(1, "acme", "live"), RepoId(1));
        var archived = Repo(2, "acme", "old");
        archived.Archived = true;
        await seed.StoreAsync(archived, RepoId(2));
        var gone = Repo(3, "acme", "gone");
        gone.Connection = RepositoryConnection.Disconnected;
        await seed.StoreAsync(gone, RepoId(3));
        await seed.StoreAsync(Repo(4, "globex", "elsewhere"), RepoId(4));
        await seed.SaveChangesAsync();
        return account;
    }

    [Fact]
    public async Task Every_connected_unarchived_repository_of_the_account_is_queued_once_per_day()
    {
        using var store = GetDocumentStore();
        var account = await SeedAsync(store);
        var bus = new RecordingMessageBus();

        using (var session = store.OpenAsyncSession())
            await new ManifestScanScheduler(session, bus, NullLogger<ManifestScanScheduler>.Instance)
                .ScheduleAccountAsync(account, waitForIndex: true);

        bus.Of<ScanRepositoryManifestsMessage>().Select(m => m.RepositoryId).Should().Equal(RepoId(1));
        bus.DeduplicationKeys.Should().ContainSingle().Which.Should().Be($"manifest-scan-{RepoId(1)}-{DateTimeOffset.UtcNow:yyyy-MM-dd}");
    }

    [Fact]
    public async Task A_failing_bus_does_not_throw_into_the_reconcile()
    {
        using var store = GetDocumentStore();
        var account = await SeedAsync(store);
        var bus = Substitute.For<MintPlayer.Spark.Messaging.Abstractions.IMessageBus>();
        bus.BroadcastOnceAsync(Arg.Any<ScanRepositoryManifestsMessage>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("database down")));

        using var session = store.OpenAsyncSession();
        await new ManifestScanScheduler(session, bus, NullLogger<ManifestScanScheduler>.Instance)
            .ScheduleAccountAsync(account, waitForIndex: true);
    }

    [Fact]
    public async Task An_installation_reconcile_schedules_the_accounts_scans()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        var forge = Substitute.For<IForgeIntegration>();
        forge.Provider.Returns(EForgeProvider.GitHub);
        var scheduler = Substitute.For<IManifestScanScheduler>();

        using (var session = store.OpenAsyncSession())
            await new ReconcileAccountRecipient(session, new SingleForgeResolver(forge), scheduler, NullLogger<ReconcileAccountRecipient>.Instance)
                .HandleAsync(new ReconcileAccountMessage { Provider = EForgeProvider.GitHub, AccountId = 77 });

        await scheduler.Received(1).ScheduleAccountAsync(Arg.Is<Account>(a => a.Login == "acme"), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_nightly_sweep_schedules_each_reconciled_accounts_scans()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        WaitForIndexing(store);
        var forge = Substitute.For<IForgeIntegration>();
        forge.Provider.Returns(EForgeProvider.GitHub);
        var scheduler = Substitute.For<IManifestScanScheduler>();

        using (var session = store.OpenAsyncSession())
            await new ReconcileForgeStateCronJob(session, new SingleForgeResolver(forge), scheduler, NullLogger<ReconcileForgeStateCronJob>.Instance)
                .RunAsync(default);

        await scheduler.Received(1).ScheduleAccountAsync(Arg.Is<Account>(a => a.Login == "acme"), false, Arg.Any<CancellationToken>());
    }
}
