using CodeCoverage.Forge;
using CodeCoverage.Entities;
using CodeCoverage.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using Octokit;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;
using Account = CodeCoverage.Entities.Account;
using Repository = CodeCoverage.Entities.Repository;

namespace CodeCoverage.Tests.Services;

/// <summary>
/// Resolving <c>owner/name</c> after a rename or a transfer.
/// <para>
/// The case worth the most attention is the collision: a repository is renamed, we remember its old
/// name, and later a brand-new repository is created at exactly that name. Both now answer to it.
/// The rule that makes this safe is ordering — a live full name is matched before any remembered
/// one — so the new repository simply shadows the alias, and nothing has to detect the conflict.
/// </para>
/// </summary>
public class RepositoryResolverTests : CoverageRavenTest
{
    protected override bool DeployIndexes => true;

    private static (RepositoryResolver Resolver, StubGitHub GitHub) CreateResolver(IAsyncDocumentSession session)
    {
        // Never consulted in these tests: resolution must not reach GitHub when we already know,
        // so reaching it at all throws, and AppClients records that it was tried.
        var github = new StubGitHub { Throws = new InvalidOperationException("resolution reached GitHub when it should not have") };
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddMemoryCache();
        services.AddSingleton(session);
        services.AddSingleton<IGitHubInstallationService>(github);
        services.AddSingleton<CodeCoverage.Services.IGitHubIndexQueries, CodeCoverage.Services.GitHubIndexQueries>();
        services.AddScoped<RepositoryResolver>();
        return (services.BuildServiceProvider().GetRequiredService<RepositoryResolver>(), github);
    }

    private static Repository Repo(long id, string fullName, params string[] previous)
        => Repo(EForgeProvider.GitHub, id, fullName, previous);

    private static Repository Repo(EForgeProvider provider, long id, string fullName, params string[] previous) => new()
    {
        GitHubId = id,
        Provider = provider,
        Name = fullName.Split('/')[1],
        FullName = fullName,
        OwnerLogin = fullName.Split('/')[0],
        PreviousFullNames = [.. previous],
    };

    private async Task<IDocumentStore> SeedAsync(params Repository[] repositories)
    {
        var store = GetDocumentStore();
        using (var session = store.OpenAsyncSession())
        {
            foreach (var repository in repositories)
            {
                // Keyed by the repository's OWN forge. The id carries the forge precisely so the
                // same numeric id on two forges is two documents, which is what the cross-forge
                // tests below need to exist at all.
                await session.StoreAsync(
                    repository, Repository.DocumentId(repository.Provider, repository.GitHubId));
            }
            await session.SaveChangesAsync();
        }
        WaitForIndexing(store);
        return store;
    }

    private async Task<IDocumentStore> SeedAccountsAsync(IDocumentStore store, params (EForgeProvider Provider, long Id, string Login)[] accounts)
    {
        using (var session = store.OpenAsyncSession())
        {
            foreach (var (provider, id, login) in accounts)
            {
                await session.StoreAsync(
                    new Account { GitHubId = id, Provider = provider, Login = login, Type = "Organization" },
                    Account.DocumentId(provider, id));
            }
            await session.SaveChangesAsync();
        }
        WaitForIndexing(store);
        return store;
    }

    [Fact]
    public async Task The_live_name_resolves_without_a_redirect()
    {
        using var store = await SeedAsync(Repo(1, "acme/widgets"));
        using var session = store.OpenAsyncSession();

        var (resolver, github) = CreateResolver(session);
        var resolution = await resolver.ResolveAsync(EForgeProvider.GitHub, "acme", "widgets", _ => true);

        resolution.Repository.Should().NotBeNull();
        resolution.Repository!.GitHubId.Should().Be(1);
        resolution.Redirect.Should().BeFalse();
        (github.AppClients > 0).Should().BeFalse();
    }

    [Fact]
    public async Task A_remembered_name_resolves_and_asks_for_a_redirect()
    {
        using var store = await SeedAsync(Repo(1, "acme/gadgets", "acme/widgets"));
        using var session = store.OpenAsyncSession();

        var (resolver, _) = CreateResolver(session);
        var resolution = await resolver.ResolveAsync(EForgeProvider.GitHub, "acme", "widgets", _ => true);

        resolution.Repository.Should().NotBeNull();
        resolution.Repository!.GitHubId.Should().Be(1);
        resolution.Redirect.Should().BeTrue();
    }

    /// <summary>
    /// The collision. Repository 1 was renamed away from <c>acme/widgets</c> and remembers it;
    /// repository 2 has since been created at that name. The live one must win, and the alias must
    /// not even be considered.
    /// </summary>
    [Fact]
    public async Task A_new_repository_at_an_old_name_shadows_the_alias()
    {
        using var store = await SeedAsync(
            Repo(1, "acme/gadgets", "acme/widgets"),
            Repo(2, "acme/widgets"));
        using var session = store.OpenAsyncSession();

        var (resolver, _) = CreateResolver(session);
        var resolution = await resolver.ResolveAsync(EForgeProvider.GitHub, "acme", "widgets", _ => true);

        resolution.Repository.Should().NotBeNull();
        resolution.Repository!.GitHubId.Should().Be(2);
        resolution.Redirect.Should().BeFalse();
    }

    /// <summary>
    /// Two repositories both once answered to this name. Guessing between them would silently serve
    /// one repository's coverage under another's URL, so the alias step declines and hands over to
    /// GitHub — which here is unavailable, so the answer is an honest miss.
    /// </summary>
    [Fact]
    public async Task An_ambiguous_alias_is_not_guessed()
    {
        using var store = await SeedAsync(
            Repo(1, "acme/one", "acme/widgets"),
            Repo(2, "acme/two", "acme/widgets"));
        using var session = store.OpenAsyncSession();

        var (resolver, _) = CreateResolver(session);
        var resolution = await resolver.ResolveAsync(EForgeProvider.GitHub, "acme", "widgets", _ => true);

        resolution.Repository.Should().BeNull();
    }

    [Fact]
    public async Task An_unknown_name_when_GitHub_is_unavailable_is_a_miss_not_an_error()
    {
        using var store = await SeedAsync(Repo(1, "acme/widgets"));
        using var session = store.OpenAsyncSession();

        var (resolver, _) = CreateResolver(session);
        var resolution = await resolver.ResolveAsync(EForgeProvider.GitHub, "nobody", "nothing", _ => true);

        resolution.Repository.Should().BeNull();
        resolution.Redirect.Should().BeFalse();
    }

    /// <summary>
    /// An owner we have never heard of must not reach GitHub at all.
    /// <para>
    /// The badge endpoint is <c>[AllowAnonymous]</c> and resolves through here, so an unguarded
    /// step three would hand an anonymous caller two things: the App's GitHub rate limit, burnable
    /// by probing distinct names until the reconciler and the PR bot start failing; and an
    /// existence oracle by response time, since a name we know answers from RavenDB while one we do
    /// not costs a network round-trip. For a private repository "we know it" means it exists and
    /// the App is installed on it — exactly what the never-404 rule refuses to disclose.
    /// </para>
    /// <para>
    /// The stand-in installation service throws if it is touched, so reaching GitHub fails the test
    /// rather than merely being slow.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_unheard_of_owner_never_reaches_GitHub()
    {
        using var store = await SeedAsync(Repo(1, "acme/widgets"));
        using var session = store.OpenAsyncSession();

        var (resolver, github) = CreateResolver(session);
        var resolution = await resolver.ResolveAsync(EForgeProvider.GitHub, "some-stranger", "anything", _ => true);

        resolution.Repository.Should().BeNull();
        (github.AppClients > 0).Should().BeFalse();
    }

    /// <summary>
    /// The case the gate must not break: the owner is known and only the repository name is stale,
    /// which is what a transfer or a rename leaves behind in a published badge URL. Here GitHub is
    /// unavailable, so the assertion is that the attempt is made at all.
    /// </summary>
    [Fact]
    public async Task A_known_owner_with_an_unknown_repository_name_does_reach_GitHub()
    {
        var store = GetDocumentStore();
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new Account { GitHubId = 5, Login = "acme" }, Account.DocumentId(EForgeProvider.GitHub, 5));
            await seed.StoreAsync(Repo(1, "acme/widgets"), Repository.DocumentId(EForgeProvider.GitHub, 1));
            await seed.SaveChangesAsync();
        }
        WaitForIndexing(store);
        using var _ = store;
        using var session = store.OpenAsyncSession();

        var (resolver, github) = CreateResolver(session);
        var resolution = await resolver.ResolveAsync(EForgeProvider.GitHub, "acme", "some-old-name", _ => true);

        resolution.Repository.Should().BeNull();
        (github.AppClients > 0).Should().BeTrue("a stale name under a known owner is exactly what step three is for");
    }

    /// <summary>
    /// The same <c>owner/name</c> on two forges is two repositories, and each URL must get its own.
    /// </summary>
    /// <remarks>
    /// ⚠ This is the defect the whole issue exists to remove, and it survived into the routes
    /// milestone: <c>ResolveAsync</c> took the provider and never used it, matching on
    /// <c>FullName</c> alone — which is unique only WITHIN a forge. A browser probe of
    /// <c>/api/browse/repos/gitlab/MintPlayer/MintPlayer.Spark</c> returned
    /// <c>Repositories/github/1006469943</c>. Anyone can register the free name on the other forge,
    /// so this resolves in the permissive direction and shows one owner's coverage under another
    /// owner's URL.
    /// </remarks>
    [Theory]
    [InlineData(EForgeProvider.GitHub, 1)]
    [InlineData(EForgeProvider.GitLab, 2)]
    public async Task The_same_full_name_on_two_forges_resolves_per_forge(EForgeProvider provider, long expected)
    {
        using var store = await SeedAsync(
            Repo(EForgeProvider.GitHub, 1, "acme/widgets"),
            Repo(EForgeProvider.GitLab, 2, "acme/widgets"));
        using var session = store.OpenAsyncSession();

        var (resolver, github) = CreateResolver(session);
        var resolution = await resolver.ResolveAsync(provider, "acme", "widgets", _ => true);

        resolution.Repository.Should().NotBeNull();
        resolution.Repository!.GitHubId.Should().Be(expected);
        resolution.Repository.Provider.Should().Be(provider);
        (github.AppClients > 0).Should().BeFalse();
    }

    /// <summary>
    /// A forge that holds no such repository resolves to nothing, even when another forge does.
    /// Every caller answers null with a 404, which is the right answer for that URL.
    /// </summary>
    [Fact]
    public async Task A_forge_without_the_repository_does_not_borrow_anothers()
    {
        using var store = await SeedAsync(Repo(EForgeProvider.GitHub, 1, "acme/widgets"));
        using var session = store.OpenAsyncSession();

        var (resolver, _) = CreateResolver(session);
        var resolution = await resolver.ResolveAsync(EForgeProvider.Bitbucket, "acme", "widgets", _ => true);

        resolution.Repository.Should().BeNull();
    }

    /// <summary>
    /// A remembered name is per-forge too — the alias step had the same blindness as the live one.
    /// </summary>
    [Fact]
    public async Task A_remembered_name_does_not_cross_forges()
    {
        using var store = await SeedAsync(Repo(EForgeProvider.GitHub, 1, "acme/widgets", "acme/gadgets"));
        using var session = store.OpenAsyncSession();

        var (resolver, _) = CreateResolver(session);

        ((await resolver.ResolveAsync(EForgeProvider.GitHub, "acme", "gadgets", _ => true)).Repository).Should().NotBeNull();
        ((await resolver.ResolveAsync(EForgeProvider.GitLab, "acme", "gadgets", _ => true)).Repository).Should().BeNull();
    }

    /// <summary>
    /// The gate in front of the GitHub name lookup is per-forge as well.
    /// </summary>
    /// <remarks>
    /// It asked "do we know an account with this login?" across every forge, so a GitHub account
    /// named <c>acme</c> admitted a <c>/gitlab/</c> URL to the step that asks GITHUB what
    /// <c>acme/unknown</c> resolves to. That would answer a GitLab path from GitHub's namespace and
    /// spend the App's rate limit doing it. <see cref="StubGitHub"/>, told to throw, makes the
    /// call observable: reaching GitHub at all throws.
    /// </remarks>
    [Fact]
    public async Task A_non_github_url_never_reaches_the_github_lookup()
    {
        using var store = await SeedAsync(Repo(EForgeProvider.GitHub, 1, "acme/widgets"));
        await SeedAccountsAsync(store, (EForgeProvider.GitHub, 7, "acme"));
        using var session = store.OpenAsyncSession();

        var (resolver, github) = CreateResolver(session);
        var resolution = await resolver.ResolveAsync(EForgeProvider.GitLab, "acme", "unknown", _ => true);

        resolution.Repository.Should().BeNull();
        (github.AppClients > 0).Should().BeFalse();
    }
}
