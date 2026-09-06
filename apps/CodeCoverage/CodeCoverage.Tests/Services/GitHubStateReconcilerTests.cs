using CodeCoverage.Entities;
using CodeCoverage.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Octokit;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;
using Account = CodeCoverage.Entities.Account;
using Repository = CodeCoverage.Entities.Repository;

namespace CodeCoverage.Tests.Services;

/// <summary>
/// The reconciler is what makes the system self-healing — and what could break it worst.
/// <para>
/// Its central inference is "ours, but absent from what the installation returned, therefore we
/// lost access". That is correct only when the list is trustworthy. If a transient failure were
/// read as absence, one sweep would disconnect every repository of every account and the site would
/// go blank; so the classification of "the installation is gone" versus "GitHub could not answer"
/// is tested before the happy path, in both directions.
/// </para>
/// </summary>
public class GitHubStateReconcilerTests : CoverageRavenTest
{
    private const long AccountId = 42;
    private const long InstallationId = 7;
    private const long KeptRepoId = 100;
    private const long GoneRepoId = 200;

    /// <summary>A stand-in installation: either it answers with a list, or it throws what GitHub would.</summary>
    private sealed class FakeInstallationRepositories : IInstallationRepositories
    {
        public List<InstallationRepository> Repositories { get; init; } = [];
        public Exception? Throws { get; init; }

        public Task<IReadOnlyList<InstallationRepository>> ListAsync(
            long installationId, int max, CancellationToken cancellationToken = default)
            => Throws is not null
                ? Task.FromException<IReadOnlyList<InstallationRepository>>(Throws)
                : Task.FromResult<IReadOnlyList<InstallationRepository>>(Repositories);
    }

    private static GitHubStateReconciler CreateReconciler(IAsyncDocumentSession session, IInstallationRepositories github)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddSingleton(session);
        services.AddSingleton(github);
        services.AddScoped<GitHubStateReconciler>();
        return services.BuildServiceProvider().GetRequiredService<GitHubStateReconciler>();
    }

    private static InstallationRepository Live(long id, string fullName)
        => new(id, fullName.Split('/')[1], fullName, fullName.Split('/')[0], false, "master", false);

    private static async Task<Account> SeedAsync(IAsyncDocumentSession session)
    {
        var account = new Account
        {
            GitHubId = AccountId,
            Login = "acme",
            Type = "Organization",
            InstallationId = InstallationId,
        };
        await session.StoreAsync(account, Account.DocumentId(AccountId));

        foreach (var (id, name) in new[] { (KeptRepoId, "kept"), (GoneRepoId, "gone") })
        {
            await session.StoreAsync(new Repository
            {
                GitHubId = id,
                Account = Account.DocumentId(AccountId),
                Name = name,
                FullName = $"acme/{name}",
                OwnerLogin = "acme",
            }, Repository.DocumentId(id));
        }

        await session.SaveChangesAsync();
        return account;
    }

    private async Task<(IDocumentStore Store, IAsyncDocumentSession Session, Account Account)> ArrangeAsync()
    {
        var store = GetDocumentStore();
        var session = store.OpenAsyncSession();
        var account = await SeedAsync(session);
        WaitForIndexing(store);
        return (store, session, account);
    }

    [Fact]
    public async Task A_repository_the_installation_no_longer_returns_is_disconnected()
    {
        var (store, session, account) = await ArrangeAsync();
        using var _ = store;
        using var __ = session;

        var github = new FakeInstallationRepositories { Repositories = { Live(KeptRepoId, "acme/kept") } };
        await CreateReconciler(session, github).ReconcileAsync(account);
        await session.SaveChangesAsync();

        var kept = await session.LoadAsync<Repository>(Repository.DocumentId(KeptRepoId));
        var gone = await session.LoadAsync<Repository>(Repository.DocumentId(GoneRepoId));

        Assert.Equal(RepositoryConnection.Connected, kept!.Connection);
        Assert.Equal(RepositoryConnection.Disconnected, gone!.Connection);
        Assert.Equal(DisconnectedReasons.RemovedFromInstallation, gone.DisconnectedReason);
    }

    [Fact]
    public async Task A_transient_GitHub_failure_changes_nothing()
    {
        var (store, session, account) = await ArrangeAsync();
        using var _ = store;
        using var __ = session;

        // The dangerous case. "GitHub did not answer" must never be read as "the installation holds
        // nothing", or one bad night disconnects every repository the service knows about.
        var github = new FakeInstallationRepositories { Throws = new HttpRequestException("connection reset") };
        await Assert.ThrowsAsync<HttpRequestException>(() => CreateReconciler(session, github).ReconcileAsync(account));

        var kept = await session.LoadAsync<Repository>(Repository.DocumentId(KeptRepoId));
        var gone = await session.LoadAsync<Repository>(Repository.DocumentId(GoneRepoId));

        Assert.Equal(RepositoryConnection.Connected, kept!.Connection);
        Assert.Equal(RepositoryConnection.Connected, gone!.Connection);
        Assert.Equal(InstallationId, (await session.LoadAsync<Account>(Account.DocumentId(AccountId)))!.InstallationId);
    }

    [Fact]
    public async Task A_server_error_is_transient_too()
    {
        var (store, session, account) = await ArrangeAsync();
        using var _ = store;
        using var __ = session;

        // A 5xx is an Octokit ApiException, the same base type as the 404 that DOES mean absence —
        // so this pins down that the classification looks at the status, not at the exception family.
        var github = new FakeInstallationRepositories
        {
            Throws = new ApiException("bad gateway", System.Net.HttpStatusCode.BadGateway),
        };
        await Assert.ThrowsAsync<ApiException>(() => CreateReconciler(session, github).ReconcileAsync(account));

        var gone = await session.LoadAsync<Repository>(Repository.DocumentId(GoneRepoId));
        Assert.Equal(RepositoryConnection.Connected, gone!.Connection);
    }

    [Fact]
    public async Task A_missing_installation_disconnects_the_whole_account()
    {
        var (store, session, account) = await ArrangeAsync();
        using var _ = store;
        using var __ = session;

        var github = new FakeInstallationRepositories { Throws = new NotFoundException("gone", System.Net.HttpStatusCode.NotFound) };
        await CreateReconciler(session, github).ReconcileAsync(account);
        await session.SaveChangesAsync();

        var reloaded = await session.LoadAsync<Account>(Account.DocumentId(AccountId));
        Assert.Null(reloaded!.InstallationId);

        foreach (var id in new[] { KeptRepoId, GoneRepoId })
        {
            var repository = await session.LoadAsync<Repository>(Repository.DocumentId(id));
            Assert.Equal(RepositoryConnection.Disconnected, repository!.Connection);
            Assert.Equal(DisconnectedReasons.AppUninstalled, repository.DisconnectedReason);
        }
    }

    [Fact]
    public async Task A_rename_we_were_never_told_about_is_repaired_and_the_old_name_remembered()
    {
        var (store, session, account) = await ArrangeAsync();
        using var _ = store;
        using var __ = session;

        var github = new FakeInstallationRepositories
        {
            Repositories = { Live(KeptRepoId, "acme/renamed"), Live(GoneRepoId, "acme/gone") },
        };
        await CreateReconciler(session, github).ReconcileAsync(account);
        await session.SaveChangesAsync();

        var repository = await session.LoadAsync<Repository>(Repository.DocumentId(KeptRepoId));
        Assert.Equal("acme/renamed", repository!.FullName);
        Assert.Equal("renamed", repository.Name);
        Assert.Contains("acme/kept", repository.PreviousFullNames);
    }

    [Fact]
    public async Task A_disconnected_repository_that_comes_back_is_reconnected()
    {
        var (store, session, account) = await ArrangeAsync();
        using var _ = store;
        using var __ = session;

        var gone = await session.LoadAsync<Repository>(Repository.DocumentId(GoneRepoId));
        gone!.Connection = RepositoryConnection.Disconnected;
        gone.DisconnectedReason = DisconnectedReasons.RemovedFromInstallation;
        gone.DisconnectedAtUtc = DateTime.UtcNow;
        await session.SaveChangesAsync();
        WaitForIndexing(store);

        var github = new FakeInstallationRepositories
        {
            Repositories = { Live(KeptRepoId, "acme/kept"), Live(GoneRepoId, "acme/gone") },
        };
        await CreateReconciler(session, github).ReconcileAsync(account);
        await session.SaveChangesAsync();

        var reloaded = await session.LoadAsync<Repository>(Repository.DocumentId(GoneRepoId));
        Assert.Equal(RepositoryConnection.Connected, reloaded!.Connection);
        Assert.Null(reloaded.DisconnectedReason);
    }
}
