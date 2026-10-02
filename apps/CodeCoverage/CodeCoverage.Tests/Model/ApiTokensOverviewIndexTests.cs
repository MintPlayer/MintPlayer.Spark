using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Indexes;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Xunit;

namespace CodeCoverage.Tests.Model;

/// <summary>
/// <see cref="ApiTokens_Overview"/> reads the owner's login, key and forge from the token's
/// <see cref="ApiToken.Account"/>, and follows the account when it changes.
/// </summary>
/// <remarks>
/// Queried with <c>ProjectInto&lt;VApiToken&gt;()</c>: without it RavenDB materialises the result
/// from the token document, where the three account fields do not exist, and they come back null
/// with no error.
/// </remarks>
public class ApiTokensOverviewIndexTests : CoverageRavenTest
{
    private static readonly string GitHubAcme = Account.DocumentId(EForgeProvider.GitHub, 7);
    private static readonly string GitLabAcme = Account.DocumentId(EForgeProvider.GitLab, 8);

    private async Task<IDocumentStore> StoreWithIndexAsync()
    {
        var store = GetDocumentStore();
        await new ApiTokens_Overview().ExecuteAsync(store);
        return store;
    }

    private static async Task SeedAsync(IDocumentStore store)
    {
        using var seed = store.OpenAsyncSession();
        await seed.StoreAsync(new Account { GitHubId = 7, Login = "acme" }, GitHubAcme);
        await seed.StoreAsync(new Account { GitHubId = 8, Login = "acme-group", Provider = EForgeProvider.GitLab }, GitLabAcme);
        await seed.StoreAsync(new ApiToken { Account = GitHubAcme, Description = "github", Hash = "h1" }, "ApiTokens/github");
        await seed.StoreAsync(new ApiToken { Account = GitLabAcme, Description = "gitlab", Hash = "h2" }, "ApiTokens/gitlab");
        await seed.StoreAsync(new ApiToken { Account = null, Description = "orphan", Hash = "h3" }, "ApiTokens/orphan");
        await seed.SaveChangesAsync();
    }

    private static async Task<VApiToken> ReadAsync(IDocumentStore store, string description)
    {
        using var session = store.OpenAsyncSession();
        return await session.Query<VApiToken, ApiTokens_Overview>()
            .Where(t => t.Description == description)
            .ProjectInto<VApiToken>()
            .SingleAsync();
    }

    [Fact]
    public async Task The_owner_fields_are_read_from_the_account()
    {
        using var store = await StoreWithIndexAsync();
        await SeedAsync(store);
        WaitForIndexing(store);

        var github = await ReadAsync(store, "github");
        github.Account.Should().Be(GitHubAcme);
        github.AccountLogin.Should().Be("acme");
        github.AccountOwnerKey.Should().Be("github:acme");
        github.Provider.Should().Be(EForgeProvider.GitHub);

        var gitlab = await ReadAsync(store, "gitlab");
        gitlab.AccountLogin.Should().Be("acme-group");
        gitlab.AccountOwnerKey.Should().Be("gitlab:acme-group");
        gitlab.Provider.Should().Be(EForgeProvider.GitLab);
    }

    /// <summary>The index's spelled-out key must equal what the rest of the app computes.</summary>
    [Fact]
    public async Task The_indexed_key_equals_the_accounts_own_OwnerKey()
    {
        using var store = await StoreWithIndexAsync();
        await SeedAsync(store);
        WaitForIndexing(store);

        using var session = store.OpenAsyncSession();
        foreach (var (description, accountId) in new[] { ("github", GitHubAcme), ("gitlab", GitLabAcme) })
        {
            var account = await session.LoadAsync<Account>(accountId);
            (await ReadAsync(store, description)).AccountOwnerKey.Should().Be(account.OwnerKey);
        }
    }

    [Fact]
    public async Task A_token_without_an_account_projects_no_owner()
    {
        using var store = await StoreWithIndexAsync();
        await SeedAsync(store);
        WaitForIndexing(store);

        var orphan = await ReadAsync(store, "orphan");
        orphan.AccountLogin.Should().BeNull();
        orphan.AccountOwnerKey.Should().BeNull();
        orphan.Provider.HasValue.Should().BeFalse();
    }

    /// <summary>
    /// The reason the fields moved off the token: a rename of the account re-indexes its tokens, so
    /// the login and key are the account's current ones rather than a copy from minting time.
    /// </summary>
    [Fact]
    public async Task Renaming_the_account_reindexes_its_tokens()
    {
        using var store = await StoreWithIndexAsync();
        await SeedAsync(store);
        WaitForIndexing(store);

        using (var rename = store.OpenAsyncSession())
        {
            (await rename.LoadAsync<Account>(GitHubAcme)).Login = "acme-renamed";
            await rename.SaveChangesAsync();
        }
        WaitForIndexing(store);

        var github = await ReadAsync(store, "github");
        github.AccountLogin.Should().Be("acme-renamed");
        github.AccountOwnerKey.Should().Be("github:acme-renamed");
        github.Account.Should().Be(GitHubAcme);
    }
}
