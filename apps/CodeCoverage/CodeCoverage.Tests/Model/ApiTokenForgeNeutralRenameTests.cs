using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;
using Xunit;

namespace CodeCoverage.Tests.Model;

/// <summary>
/// The <c>ApiToken</c> field renames, seeded in the shape production actually stores.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>Every fixture here writes the PRE-RENAME JSON</b> — <c>AccountGitHubId</c> and
/// <c>GithubRepositories</c>, with no <c>AccountId</c>, no <c>RepositoryIds</c> and no
/// <c>Provider</c>. That is the only state these migrations will ever meet, and constructing an
/// <c>ApiToken</c> in C# cannot produce it: the entity no longer declares the old names, so a C#
/// fixture silently writes the new shape and every assertion becomes a tautology.
/// </para>
/// <para>
/// That omission hid a real ordering bug. The backfill originally ran <em>before</em> this rename,
/// so on production data <c>token.AccountId</c> deserialized as null for every legacy token, its
/// "already stamped" guard was dead, and the RavenDB client's save — which re-serializes the entity
/// — would have deleted the authoritative <c>AccountGitHubId</c> on the way past.
/// </para>
/// </remarks>
public class ApiTokenForgeNeutralRenameTests : CoverageRavenTest
{
    private static Task RunRenameAsync(IDocumentStore store)
        => new M_202609220950_ApiTokenFieldsAreForgeNeutral(
            store, NullLogger<M_202609220950_ApiTokenFieldsAreForgeNeutral>.Instance)
        .UpAsync(CancellationToken.None);

    /// <summary>
    /// Writes a token document in the pre-rename shape, through a patch rather than the entity.
    /// </summary>
    private static async Task SeedLegacyTokenAsync(
        IDocumentStore store, string id, long? accountGitHubId, params string[] repositories)
    {
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new ApiToken { Description = "ci", Scope = "Account", AccountLogin = "acme" }, id);
            await seed.SaveChangesAsync();
        }

        // Strip the new names and write the old ones, so the document looks like one stored before
        // this PR existed.
        var patch = store.Operations.Send(new PatchByQueryOperation(new IndexQuery
        {
            Query = $$"""
                from ApiTokens as d where id(d) = '{{id}}' update {
                    delete d.AccountId;
                    delete d.RepositoryIds;
                    delete d.Provider;
                    {{(accountGitHubId is { } a ? $"d.AccountGitHubId = {a};" : "")}}
                    d.GithubRepositories = {{System.Text.Json.JsonSerializer.Serialize(repositories)}};
                }
                """,
        }));
        patch.WaitForCompletion<BulkOperationResult>(TimeSpan.FromSeconds(30));
    }

    private static async Task<(long? AccountId, List<string> RepositoryIds, EForgeProvider Provider)>
        ReadAsync(IDocumentStore store, string id)
    {
        using var verify = store.OpenAsyncSession();
        var token = await verify.LoadAsync<ApiToken>(id);
        return (token.AccountId, token.RepositoryIds, token.Provider);
    }

    [Fact]
    public async Task The_numeric_account_id_survives_the_rename()
    {
        using var store = GetDocumentStore();
        await SeedLegacyTokenAsync(store, "ApiTokens/1-A", accountGitHubId: 48772716);

        await RunRenameAsync(store);

        var (accountId, _, _) = await ReadAsync(store, "ApiTokens/1-A");
        Assert.Equal(48772716, accountId);
    }

    [Fact]
    public async Task The_repository_list_survives_the_rename()
    {
        using var store = GetDocumentStore();
        await SeedLegacyTokenAsync(store, "ApiTokens/1-A", accountGitHubId: 7,
            "Repositories/github/1", "Repositories/github/2");

        await RunRenameAsync(store);

        var (_, repositories, _) = await ReadAsync(store, "ApiTokens/1-A");
        Assert.Equal(["Repositories/github/1", "Repositories/github/2"], repositories);
    }

    [Fact]
    public async Task Every_existing_token_becomes_a_GitHub_token()
    {
        using var store = GetDocumentStore();
        await SeedLegacyTokenAsync(store, "ApiTokens/1-A", accountGitHubId: 7);

        await RunRenameAsync(store);

        var (_, _, provider) = await ReadAsync(store, "ApiTokens/1-A");
        Assert.Equal(EForgeProvider.GitHub, provider);
    }

    /// <summary>
    /// ⚠️ The ordering bug itself: after the rename, the backfill's "already stamped" guard must see
    /// the id. Before the renumber it saw null and re-derived every token from its login.
    /// </summary>
    [Fact]
    public async Task After_the_rename_the_backfill_leaves_an_already_stamped_token_alone()
    {
        using var store = GetDocumentStore();

        // An account whose login matches but whose numeric id does NOT — standing in for a login
        // the forge has since reassigned. If the backfill re-derives, it writes 999 over 7.
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(
                new Account { GitHubId = 999, Login = "acme", Provider = EForgeProvider.GitHub },
                Account.DocumentId(EForgeProvider.GitHub, 999));
            await seed.SaveChangesAsync();
        }

        await SeedLegacyTokenAsync(store, "ApiTokens/1-A", accountGitHubId: 7);
        using (var setKey = store.OpenAsyncSession())
        {
            var token = await setKey.LoadAsync<ApiToken>("ApiTokens/1-A");
            token.AccountOwnerKey = new ForgeOwner(EForgeProvider.GitHub, "acme").ToString();
            await setKey.SaveChangesAsync();
        }

        await RunRenameAsync(store);
        await new M_202609221000_BackfillApiTokenAccountIds(
            store, NullLogger<M_202609221000_BackfillApiTokenAccountIds>.Instance)
            .UpAsync(CancellationToken.None);

        var (accountId, _, _) = await ReadAsync(store, "ApiTokens/1-A");
        Assert.Equal(7, accountId);
    }

    [Fact]
    public async Task Running_the_rename_twice_changes_nothing()
    {
        using var store = GetDocumentStore();
        await SeedLegacyTokenAsync(store, "ApiTokens/1-A", accountGitHubId: 42, "Repositories/github/1");

        await RunRenameAsync(store);
        await RunRenameAsync(store);

        var (accountId, repositories, provider) = await ReadAsync(store, "ApiTokens/1-A");
        Assert.Equal(42, accountId);
        Assert.Equal(["Repositories/github/1"], repositories);
        Assert.Equal(EForgeProvider.GitHub, provider);
    }

    /// <summary>
    /// A token that never had a numeric id keeps not having one — the rename must not invent a zero.
    /// </summary>
    [Fact]
    public async Task A_token_with_no_numeric_id_is_left_null_not_zero()
    {
        using var store = GetDocumentStore();
        await SeedLegacyTokenAsync(store, "ApiTokens/1-A", accountGitHubId: null);

        await RunRenameAsync(store);

        var (accountId, _, _) = await ReadAsync(store, "ApiTokens/1-A");
        Assert.Null(accountId);
    }
}
