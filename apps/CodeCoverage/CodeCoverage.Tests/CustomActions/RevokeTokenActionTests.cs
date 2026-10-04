using System.Security.Claims;
using CodeCoverage.CustomActions;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.ClientOperations;
using NSubstitute;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.CustomActions;

/// <summary>
/// Revoking an upload token — the ownership check, and that a permitted revoke actually revokes.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>This file did not exist while revoke was broken for every user on production.</b> The
/// action passed <c>token.AccountLogin</c> ("acme") to <c>CanManageOwnerAsync</c>, which compares
/// owner KEYS ("github:acme"), so the ownership check refused unconditionally and the button did
/// nothing but show an error. Fail-closed, so nothing alerted.
/// </para>
/// <para>
/// The double below is <b>argument-sensitive</b>: it answers only for the key the real
/// <c>SparkVisibility</c> would have produced. A stub returning a fixed bool for
/// <c>Arg.Any&lt;string&gt;()</c> cannot detect that the wrong value was passed, which is exactly
/// how the sibling delete-data bug survived a test file named after it.
/// </para>
/// </remarks>
public class RevokeTokenActionTests : CoverageRavenTest
{
    private const string Owner = "acme";
    private const string TokenId = "ApiTokens/1-A";

    private static string OwnerKey => new ForgeOwner(EForgeProvider.GitHub, Owner).ToString();

    private sealed record Harness(RevokeTokenAction Action, IClientAccessor Client);

    private static Harness CreateAction(IAsyncDocumentSession session, bool canManageOwner)
    {
        var client = Substitute.For<IClientAccessor>();
        var manager = Substitute.For<IManager>();
        manager.Client.Returns(client);

        var visibility = Substitute.For<ISparkVisibility>();
        visibility.CanManageOwnerAsync(Arg.Any<string>())
            .Returns(call => Task.FromResult(
                canManageOwner && string.Equals(call.Arg<string>(), OwnerKey, StringComparison.OrdinalIgnoreCase)));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(session);
        services.AddSingleton(manager);
        services.AddSingleton(visibility);
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) },
        });
        services.AddScoped<RevokeTokenAction>();

        return new Harness(services.BuildServiceProvider().GetRequiredService<RevokeTokenAction>(), client);
    }

    private static readonly string OwnerAccount = Account.DocumentId(EForgeProvider.GitHub, 1);
    private static readonly string OtherAccount = Account.DocumentId(EForgeProvider.GitHub, 2);

    /// <summary>
    /// The token names its account by document id; the action reads the account's owner KEY from
    /// the account itself, so both accounts exist here.
    /// </summary>
    private static async Task SeedAsync(IDocumentStore store, DateTime? revokedAt = null, string? account = null)
    {
        using var session = store.OpenAsyncSession();
        await session.StoreAsync(new Account { GitHubId = 1, Login = Owner }, OwnerAccount);
        await session.StoreAsync(new Account { GitHubId = 2, Login = "someone-else" }, OtherAccount);
        await session.StoreAsync(new ApiToken
        {
            Description = "ci",
            Account = account ?? OwnerAccount,
            Scope = "Account",
            RevokedAtUtc = revokedAt,
        }, TokenId);
        await session.SaveChangesAsync();
    }

    /// <summary>The rows ticked on the account's upload-token card (#467, D22: a query action).</summary>
    private static CustomActionArgs ArgsFor(params string?[] tokenIds) => new()
    {
        SelectedItems = [.. tokenIds.Where(id => id is not null).Select(id => new QueryResultItem { Id = id!, Values = [] })],
    };

    private static async Task<DateTime?> RevokedAtAsync(IDocumentStore store)
    {
        using var verify = store.OpenAsyncSession();
        return (await verify.LoadAsync<ApiToken>(TokenId)).RevokedAtUtc;
    }

    [Fact]
    public async Task A_manager_of_the_account_revokes_the_token()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);

        using (var session = store.OpenAsyncSession())
            await CreateAction(session, canManageOwner: true).Action.ExecuteAsync(ArgsFor(TokenId));

        Assert.NotNull(await RevokedAtAsync(store));
    }

    /// <summary>
    /// The escalation attempt, and the one that must keep working whatever else changes.
    /// </summary>
    [Fact]
    public async Task Someone_who_does_not_manage_the_account_is_refused()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);

        using (var session = store.OpenAsyncSession())
            await CreateAction(session, canManageOwner: false).Action.ExecuteAsync(ArgsFor(TokenId));

        Assert.Null(await RevokedAtAsync(store));
    }

    /// <summary>
    /// The caller manages <c>acme</c>, but the token belongs to another account: refused. The key
    /// checked is the TOKEN's account's, read from that account, not anything the caller supplies.
    /// </summary>
    [Fact]
    public async Task A_token_of_an_account_the_caller_does_not_manage_is_refused()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, account: OtherAccount);

        using (var session = store.OpenAsyncSession())
            await CreateAction(session, canManageOwner: true).Action.ExecuteAsync(ArgsFor(TokenId));

        Assert.Null(await RevokedAtAsync(store));
    }

    /// <summary>
    /// A token with no account, or one whose account no longer exists, cannot be attributed to
    /// anyone, so it cannot be revoked through this path either — the check must not fall open on a null.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("Accounts/github/404")]
    public async Task A_token_without_a_resolvable_account_is_refused_rather_than_allowed(string? account)
    {
        using var store = GetDocumentStore();
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new ApiToken { Description = "orphan", Account = account, Scope = "Account" }, TokenId);
            await seed.SaveChangesAsync();
        }

        using (var session = store.OpenAsyncSession())
            await CreateAction(session, canManageOwner: true).Action.ExecuteAsync(ArgsFor(TokenId));

        Assert.Null(await RevokedAtAsync(store));
    }

    [Fact]
    public async Task An_already_revoked_token_keeps_its_original_timestamp()
    {
        using var store = GetDocumentStore();
        var original = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await SeedAsync(store, revokedAt: original);

        using (var session = store.OpenAsyncSession())
            await CreateAction(session, canManageOwner: true).Action.ExecuteAsync(ArgsFor(TokenId));

        Assert.Equal(original, await RevokedAtAsync(store));
    }

    /// <summary>
    /// Every exit notifies — a silent return is indistinguishable from success in the browser, which
    /// the action's own remarks call out.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Every_outcome_tells_the_user_something(bool canManageOwner)
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);

        Harness harness;
        using (var session = store.OpenAsyncSession())
        {
            harness = CreateAction(session, canManageOwner);
            await harness.Action.ExecuteAsync(ArgsFor(TokenId));
        }

        harness.Client.ReceivedWithAnyArgs().Notify(default(string)!, default);
    }

    /// <summary>
    /// A selection is revoked entirely or not at all: one token of an account the caller does not
    /// manage refuses the whole request, so the token they do manage keeps working too.
    /// </summary>
    [Fact]
    public async Task One_token_the_caller_does_not_manage_refuses_the_whole_selection()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        const string foreignToken = "ApiTokens/2-A";
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new ApiToken { Description = "theirs", Account = OtherAccount, Scope = "Account" }, foreignToken);
            await seed.SaveChangesAsync();
        }

        using (var session = store.OpenAsyncSession())
            await CreateAction(session, canManageOwner: true).Action.ExecuteAsync(ArgsFor(TokenId, foreignToken));

        Assert.Null(await RevokedAtAsync(store));
        using var verify = store.OpenAsyncSession();
        Assert.Null((await verify.LoadAsync<ApiToken>(foreignToken)).RevokedAtUtc);
    }

    [Fact]
    public async Task Every_selected_token_the_caller_manages_is_revoked()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        const string secondToken = "ApiTokens/3-A";
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new ApiToken { Description = "second", Account = OwnerAccount, Scope = "Account" }, secondToken);
            await seed.SaveChangesAsync();
        }

        using (var session = store.OpenAsyncSession())
            await CreateAction(session, canManageOwner: true).Action.ExecuteAsync(ArgsFor(TokenId, secondToken));

        Assert.NotNull(await RevokedAtAsync(store));
        using var verify = store.OpenAsyncSession();
        Assert.NotNull((await verify.LoadAsync<ApiToken>(secondToken)).RevokedAtUtc);
    }

    [Fact]
    public async Task No_token_in_context_is_reported_rather_than_ignored()
    {
        using var store = GetDocumentStore();

        Harness harness;
        using (var session = store.OpenAsyncSession())
        {
            harness = CreateAction(session, canManageOwner: true);
            await harness.Action.ExecuteAsync(ArgsFor());
        }

        harness.Client.ReceivedWithAnyArgs().Notify(default(string)!, default);
    }
}
