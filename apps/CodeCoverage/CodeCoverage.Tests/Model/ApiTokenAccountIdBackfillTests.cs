using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.Model;

/// <summary>
/// Backfilling <c>ApiToken.AccountGitHubId</c> — what resolves, and what deliberately does not.
/// </summary>
/// <remarks>
/// <para>
/// M6g was blocked on a precondition nobody had tested: it assumed the #422 migration backfilled
/// this field, and nothing did. The unresolvable cases are the interesting half, because they are
/// the exact population that keeps the login-comparison fallback alive — the fallback cannot be
/// deleted while any of them exist, so they must be countable rather than invisible.
/// </para>
/// <para>
/// ⚠️ <b>Resolution goes through the owner KEY, never the bare login.</b> Two forges can host the
/// same login, and stamping a token with the wrong forge's numeric id would authorize uploads for
/// somebody else's repositories.
/// </para>
/// </remarks>
public class ApiTokenAccountIdBackfillTests : CoverageRavenTest
{
    private const long AccountId = 48772716;
    private const string Login = "MintPlayer";

    private static string KeyFor(string login, EForgeProvider provider = EForgeProvider.GitHub)
        => new ForgeOwner(provider, login).ToString();

    private static Task RunAsync(IDocumentStore store)
        => new M_202609221000_BackfillApiTokenAccountIds(
            store, NullLogger<M_202609221000_BackfillApiTokenAccountIds>.Instance)
        .UpAsync(CancellationToken.None);

    private static async Task SeedAccountAsync(IDocumentStore store, string login = Login,
        EForgeProvider provider = EForgeProvider.GitHub, long id = AccountId)
    {
        using var session = store.OpenAsyncSession();
        await session.StoreAsync(new Account { GitHubId = id, Login = login, Provider = provider },
            Account.DocumentId(provider, id));
        await session.SaveChangesAsync();
    }

    private static async Task SeedTokenAsync(IDocumentStore store, string id, string? ownerKey,
        string scope = "Account", long? accountGitHubId = null)
    {
        using var session = store.OpenAsyncSession();
        await session.StoreAsync(new ApiToken
        {
            Description = "ci",
            Scope = scope,
            AccountLogin = Login,
            AccountOwnerKey = ownerKey,
            AccountGitHubId = accountGitHubId,
        }, id);
        await session.SaveChangesAsync();
    }

    private static async Task<long?> AccountIdOfAsync(IDocumentStore store, string id)
    {
        using var verify = store.OpenAsyncSession();
        return (await verify.LoadAsync<ApiToken>(id)).AccountGitHubId;
    }

    [Fact]
    public async Task A_token_with_a_resolvable_owner_key_is_stamped()
    {
        using var store = GetDocumentStore();
        await SeedAccountAsync(store);
        await SeedTokenAsync(store, "ApiTokens/1-A", KeyFor(Login));

        await RunAsync(store);

        Assert.Equal(AccountId, await AccountIdOfAsync(store, "ApiTokens/1-A"));
    }

    [Fact]
    public async Task A_token_that_already_has_an_id_is_left_alone()
    {
        using var store = GetDocumentStore();
        await SeedAccountAsync(store);
        await SeedTokenAsync(store, "ApiTokens/1-A", KeyFor(Login), accountGitHubId: 999);

        await RunAsync(store);

        Assert.Equal(999, await AccountIdOfAsync(store, "ApiTokens/1-A"));
    }

    /// <summary>
    /// A repository-scoped token authorizes on its repository claims and never reads this field.
    /// </summary>
    [Fact]
    public async Task A_repository_scoped_token_is_skipped()
    {
        using var store = GetDocumentStore();
        await SeedAccountAsync(store);
        await SeedTokenAsync(store, "ApiTokens/1-A", KeyFor(Login), scope: "Repository");

        await RunAsync(store);

        Assert.Null(await AccountIdOfAsync(store, "ApiTokens/1-A"));
    }

    // ── The unresolvable population, which is what M6g actually needs to know about ─────────────

    [Fact]
    public async Task A_token_with_no_owner_key_cannot_be_resolved()
    {
        using var store = GetDocumentStore();
        await SeedAccountAsync(store);
        await SeedTokenAsync(store, "ApiTokens/1-A", ownerKey: null);

        await RunAsync(store);

        Assert.Null(await AccountIdOfAsync(store, "ApiTokens/1-A"));
    }

    [Fact]
    public async Task A_token_whose_account_was_renamed_or_deleted_cannot_be_resolved()
    {
        using var store = GetDocumentStore();
        await SeedAccountAsync(store, login: "MintPlayer-Renamed");
        await SeedTokenAsync(store, "ApiTokens/1-A", KeyFor(Login));

        await RunAsync(store);

        // The stored login names no account any more, and the numeric id it should map to is the
        // one thing a rename preserves and the one thing was never recorded.
        Assert.Null(await AccountIdOfAsync(store, "ApiTokens/1-A"));
    }

    /// <summary>
    /// ⚠️ The case that makes the key mandatory: a same-named account on a different forge must not
    /// satisfy the lookup, or the token would be stamped with a stranger's numeric id.
    /// </summary>
    [Fact]
    public async Task A_same_named_account_on_another_forge_does_not_resolve_it()
    {
        using var store = GetDocumentStore();
        await SeedAccountAsync(store, provider: EForgeProvider.GitLab, id: 555);
        await SeedTokenAsync(store, "ApiTokens/1-A", KeyFor(Login));

        await RunAsync(store);

        Assert.Null(await AccountIdOfAsync(store, "ApiTokens/1-A"));
    }

    [Fact]
    public async Task An_unresolvable_token_is_never_revoked_by_the_migration()
    {
        using var store = GetDocumentStore();
        await SeedTokenAsync(store, "ApiTokens/1-A", ownerKey: null);

        await RunAsync(store);

        using var verify = store.OpenAsyncSession();
        var token = await verify.LoadAsync<ApiToken>("ApiTokens/1-A");

        // A token that still works is one somebody is using. Breaking an upload pipeline is not a
        // decision a migration takes on its own.
        Assert.Null(token.RevokedAtUtc);
    }

    /// <summary>
    /// Several tokens at once, mixed resolvable and not — the shape production will actually have,
    /// and the one that exercises the periodic flush.
    /// </summary>
    [Fact]
    public async Task A_mixed_population_stamps_only_what_it_can()
    {
        using var store = GetDocumentStore();
        await SeedAccountAsync(store);
        await SeedTokenAsync(store, "ApiTokens/1-A", KeyFor(Login));
        await SeedTokenAsync(store, "ApiTokens/2-A", KeyFor("someone-else"));
        await SeedTokenAsync(store, "ApiTokens/3-A", ownerKey: null);

        await RunAsync(store);

        Assert.Equal(AccountId, await AccountIdOfAsync(store, "ApiTokens/1-A"));
        Assert.Null(await AccountIdOfAsync(store, "ApiTokens/2-A"));
        Assert.Null(await AccountIdOfAsync(store, "ApiTokens/3-A"));
    }

    [Fact]
    public async Task Running_it_twice_changes_nothing()
    {
        using var store = GetDocumentStore();
        await SeedAccountAsync(store);
        await SeedTokenAsync(store, "ApiTokens/1-A", KeyFor(Login));

        await RunAsync(store);
        await RunAsync(store);

        Assert.Equal(AccountId, await AccountIdOfAsync(store, "ApiTokens/1-A"));
    }
}
