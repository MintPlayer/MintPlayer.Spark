using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using Microsoft.Extensions.Configuration;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.Services;

/// <summary>
/// D4: the accounts list is scoped per forge, not unioned.
///
/// <para>
/// The corpus here is the case the whole issue is about — <b>the same login on two forges</b>.
/// `mintplayer` on GitHub and `mintplayer` on GitLab are unrelated principals that anyone can
/// register, and every bug this milestone exists to prevent looks like one of them being shown
/// where the other was asked for.
/// </para>
/// </summary>
public class MyAccountsProviderScopeTests : CoverageRavenTest
{
    private const string Login = "mintplayer";

    /// <summary>
    /// A resolver over several forges. <see cref="ScriptedDiffService"/> is its own resolver, which
    /// is right for the tests that have one forge and not enough for this one.
    /// </summary>
    /// <remarks>
    /// ⚠ It resolves real <see cref="IForgeIntegration"/> instances rather than answering the
    /// owner-set question itself. <c>GetAllowedOwnerKeysAsync</c> is an EXTENSION method on the
    /// interface (<c>ForgeFanOut</c>), not a member of it, so a fake that declares a method of the
    /// same name compiles, binds to nothing, and is silently never called — the extension runs
    /// instead and answers from <c>For(provider)</c>. This shape makes the production fan-out the
    /// thing under test, which is what a scoping test should be exercising anyway.
    /// </remarks>
    private sealed class MultiForgeResolver : IForgeIntegrationResolver
    {
        private readonly Dictionary<EForgeProvider, ScriptedDiffService> forges = [];

        public MultiForgeResolver(params (EForgeProvider Provider, string[] Logins)[] definitions)
        {
            foreach (var (provider, logins) in definitions)
            {
                var forge = new ScriptedDiffService { Provider = provider };
                forge.Owners.AddRange(logins.Select(l => new ForgeOwner(provider, l)));
                forges[provider] = forge;
            }
        }

        public IForgeIntegration? For(EForgeProvider provider)
            => forges.TryGetValue(provider, out var forge) ? forge : null;

        public IForgeIntegration For(Repository repository) => forges[repository.Provider];

        public IReadOnlyList<IForgeIntegration> All => [.. forges.Values];

        public Task<IReadOnlyList<EForgeProvider>> GetLinkedProvidersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<EForgeProvider>>([.. forges.Keys]);
    }

    private static Repository Repo(EForgeProvider provider, long id, string owner, string name) => new()
    {
        GitHubId = id,
        Provider = provider,
        Name = name,
        FullName = $"{owner}/{name}",
        OwnerLogin = owner,
        Connection = RepositoryConnection.Connected,
    };

    private async Task<IDocumentStore> Seed()
    {
        var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();

        // The SAME numeric id on both forges as well as the same login: ids are only unique per
        // forge, so a scheme that keys on the number alone collapses these two into one document.
        await session.StoreAsync(
            new Account { GitHubId = 42, Provider = EForgeProvider.GitHub, Login = Login, Type = "Organization" },
            Account.DocumentId(EForgeProvider.GitHub, 42));
        await session.StoreAsync(
            new Account { GitHubId = 42, Provider = EForgeProvider.GitLab, Login = Login, Type = "Organization" },
            Account.DocumentId(EForgeProvider.GitLab, 42));

        // Two on GitHub, one on GitLab, so a unioned count (3) is distinguishable from either
        // scoped count (2 and 1) rather than coinciding with one of them.
        foreach (var repo in new[]
        {
            Repo(EForgeProvider.GitHub, 1, Login, "spark"),
            Repo(EForgeProvider.GitHub, 2, Login, "coverage"),
            Repo(EForgeProvider.GitLab, 3, Login, "unrelated"),
        })
        {
            await session.StoreAsync(repo, Repository.DocumentId(repo.Provider, repo.GitHubId));
        }

        await session.SaveChangesAsync();
        WaitForIndexing(store);
        return store;
    }

    private static MyAccountsService Service(IDocumentStore store, IForgeIntegrationResolver forges)
        => new(store.OpenAsyncSession(), forges, new ConfigurationBuilder().Build(), new FakeWebHostEnvironment());

    [Fact]
    public async Task An_unscoped_call_still_fans_out_across_every_linked_forge()
    {
        using var store = await Seed();
        var service = Service(store, new MultiForgeResolver(
            (EForgeProvider.GitHub, [Login]), (EForgeProvider.GitLab, [Login])));

        var result = await service.GetAsync(CancellationToken.None);

        // Two rows for one login: that is the point of the key, not a duplicate.
        result.Accounts.Should().HaveCount(2);
        result.Accounts.Sum(a => a.RepoCount).Should().Be(3);
    }

    [Theory]
    [InlineData(EForgeProvider.GitHub, "github", 2)]
    [InlineData(EForgeProvider.GitLab, "gitlab", 1)]
    public async Task A_scoped_call_sees_only_that_forge(EForgeProvider scope, string canonical, int expectedRepos)
    {
        using var store = await Seed();
        var service = Service(store, new MultiForgeResolver(
            (EForgeProvider.GitHub, [Login]), (EForgeProvider.GitLab, [Login])));

        var result = await service.GetAsync(CancellationToken.None, provider: scope);

        result.Accounts.Should().HaveCount(1);
        var row = result.Accounts[0];
        row.Provider.Should().Be(canonical);
        row.RepoCount.Should().Be(expectedRepos);

        // The DISPLAYED login stays unqualified — the owner key must never reach a label.
        row.Login.Should().Be(Login);
    }

    /// <summary>
    /// The row id is the owner key, not the login. With the login it would collide across forges,
    /// and the projector throws by name on a duplicate row id — so the merged Home page would not
    /// merely look confusing with a second forge linked, it would fail to render at all.
    /// </summary>
    [Fact]
    public async Task Rows_for_the_same_login_on_two_forges_have_distinct_ids()
    {
        using var store = await Seed();
        var service = Service(store, new MultiForgeResolver(
            (EForgeProvider.GitHub, [Login]), (EForgeProvider.GitLab, [Login])));

        var result = await service.GetAsync(CancellationToken.None);

        result.Accounts.Select(a => a.Id).Distinct().Should().HaveCount(2);
        result.Accounts.Select(a => a.Id).Should().Contain($"github:{Login}");
        result.Accounts.Select(a => a.Id).Should().Contain($"gitlab:{Login}");
    }
}
