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
/// Since 2026-10-02 the entity no longer declares <c>AccountId</c> or <c>Provider</c> either
/// (<c>M_202610021200_ApiTokenReferencesItsAccount</c> folds them into <c>Account</c>), so the
/// renamed fields are read back through <see cref="TokenFields"/>, a test-local view of the raw
/// document.
/// </para>
/// </remarks>
public class ApiTokenForgeNeutralRenameTests : CoverageRavenTest
{
    /// <summary>The fields this migration writes, read straight off the stored JSON.</summary>
    private sealed class TokenFields
    {
        public long? AccountId { get; set; }
        public List<string> RepositoryIds { get; set; } = [];
        public string? Provider { get; set; }
        public string? Account { get; set; }
    }

    private static Task RunRenameAsync(IDocumentStore store)
        => new M_202609220950_ApiTokenFieldsAreForgeNeutral(
            store, NullLogger<M_202609220950_ApiTokenFieldsAreForgeNeutral>.Instance)
        .UpAsync(CancellationToken.None);

    private static Task RunAccountReferenceAsync(IDocumentStore store)
        => new M_202610021200_ApiTokenReferencesItsAccount(
            store, NullLogger<M_202610021200_ApiTokenReferencesItsAccount>.Instance)
        .UpAsync(CancellationToken.None);

    /// <summary>
    /// Writes a token document in the pre-rename shape, through a patch rather than the entity.
    /// </summary>
    private static async Task SeedLegacyTokenAsync(
        IDocumentStore store, string id, long? accountGitHubId, params string[] repositories)
    {
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new ApiToken { Description = "ci", Scope = "Account" }, id);
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
                    delete d.Account;
                    d.AccountLogin = 'acme';
                    {{(accountGitHubId is { } a ? $"d.AccountGitHubId = {a};" : "")}}
                    d.GithubRepositories = {{System.Text.Json.JsonSerializer.Serialize(repositories)}};
                }
                """,
        }));
        patch.WaitForCompletion<BulkOperationResult>(TimeSpan.FromSeconds(30));
    }

    private static async Task<TokenFields> ReadAsync(IDocumentStore store, string id)
    {
        using var verify = store.OpenAsyncSession();
        return await verify.LoadAsync<TokenFields>(id);
    }

    /// <summary>
    /// ⚠️ The ordering itself, which every other test here only assumes.
    /// </summary>
    /// <remarks>
    /// <c>SparkMigrationRegistry</c> orders by <c>Version</c>. The account-reference migration reads
    /// <c>AccountId</c> and <c>Provider</c> — the names this one writes — so it must run after it,
    /// or every legacy token falls through to the weaker owner-key and login lookups.
    /// </remarks>
    [Fact]
    public void The_rename_is_ordered_before_the_account_reference_migration()
    {
        (M_202609220950_ApiTokenFieldsAreForgeNeutral.Version
                < M_202610021200_ApiTokenReferencesItsAccount.Version).Should().BeTrue("The rename must run first, or the account migration never sees the numeric id.");
    }

    [Fact]
    public async Task The_numeric_account_id_survives_the_rename()
    {
        using var store = GetDocumentStore();
        await SeedLegacyTokenAsync(store, "ApiTokens/1-A", accountGitHubId: 48772716);

        await RunRenameAsync(store);

        var token = await ReadAsync(store, "ApiTokens/1-A");
        token.AccountId.Should().Be(48772716);
    }

    [Fact]
    public async Task The_repository_list_survives_the_rename()
    {
        using var store = GetDocumentStore();
        await SeedLegacyTokenAsync(store, "ApiTokens/1-A", accountGitHubId: 7,
            "Repositories/github/1", "Repositories/github/2");

        await RunRenameAsync(store);

        var token = await ReadAsync(store, "ApiTokens/1-A");
        token.RepositoryIds.Should().Equal(["Repositories/github/1", "Repositories/github/2"]);
    }

    /// <summary>The provider is written as the enum NAME, which is what the account migration parses.</summary>
    [Fact]
    public async Task Every_existing_token_becomes_a_GitHub_token()
    {
        using var store = GetDocumentStore();
        await SeedLegacyTokenAsync(store, "ApiTokens/1-A", accountGitHubId: 7);

        await RunRenameAsync(store);

        var token = await ReadAsync(store, "ApiTokens/1-A");
        token.Provider.Should().Be(nameof(EForgeProvider.GitHub));
    }

    /// <summary>
    /// ⚠️ The chain end to end, in runner order: the renamed id/provider pair decides the account,
    /// even when the login now names a different account.
    /// </summary>
    /// <remarks>
    /// Account 999 holds the login <c>acme</c> today (the forge reassigned it); account 7 is the one
    /// the token was minted for. Run in the wrong order — or with the pair lost by the rename — the
    /// login lookup would file the token under 999.
    /// </remarks>
    [Fact]
    public async Task After_the_rename_the_account_migration_resolves_the_numeric_id()
    {
        using var store = GetDocumentStore();
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new Account { GitHubId = 999, Login = "acme" }, Account.DocumentId(EForgeProvider.GitHub, 999));
            await seed.StoreAsync(new Account { GitHubId = 7, Login = "acme-renamed" }, Account.DocumentId(EForgeProvider.GitHub, 7));
            await seed.SaveChangesAsync();
        }
        await SeedLegacyTokenAsync(store, "ApiTokens/1-A", accountGitHubId: 7);

        await RunRenameAsync(store);
        await RunAccountReferenceAsync(store);

        var token = await ReadAsync(store, "ApiTokens/1-A");
        token.Account.Should().Be(Account.DocumentId(EForgeProvider.GitHub, 7));
        token.AccountId.HasValue.Should().BeFalse();
        token.Provider.Should().BeNull();
    }

    [Fact]
    public async Task Running_the_rename_twice_changes_nothing()
    {
        using var store = GetDocumentStore();
        await SeedLegacyTokenAsync(store, "ApiTokens/1-A", accountGitHubId: 42, "Repositories/github/1");

        await RunRenameAsync(store);
        await RunRenameAsync(store);

        var token = await ReadAsync(store, "ApiTokens/1-A");
        token.AccountId.Should().Be(42);
        token.RepositoryIds.Should().Equal(["Repositories/github/1"]);
        token.Provider.Should().Be(nameof(EForgeProvider.GitHub));
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

        var token = await ReadAsync(store, "ApiTokens/1-A");
        token.AccountId.HasValue.Should().BeFalse();
    }
}
