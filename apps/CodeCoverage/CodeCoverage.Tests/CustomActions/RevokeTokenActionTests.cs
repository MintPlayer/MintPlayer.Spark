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

    private static async Task SeedAsync(IDocumentStore store, DateTime? revokedAt = null)
    {
        using var session = store.OpenAsyncSession();
        await session.StoreAsync(new ApiToken
        {
            Description = "ci",
            AccountLogin = Owner,
            AccountOwnerKey = OwnerKey,
            Scope = "Account",
            RevokedAtUtc = revokedAt,
        }, TokenId);
        await session.SaveChangesAsync();
    }

    private static CustomActionArgs ArgsFor(string? tokenId) => new()
    {
        Parent = tokenId is null ? null : new PersistentObject
        {
            Id = tokenId,
            Name = "ApiToken",
            ObjectTypeId = Guid.Empty,
            Attributes = [],
        },
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
    /// A token with no owner key cannot be attributed to anyone, so it cannot be revoked through
    /// this path either — the check must not fall open on a null.
    /// </summary>
    [Fact]
    public async Task A_token_with_no_owner_key_is_refused_rather_than_allowed()
    {
        using var store = GetDocumentStore();
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new ApiToken { Description = "orphan", AccountOwnerKey = null, Scope = "Account" }, TokenId);
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

        harness.Client.ReceivedWithAnyArgs().Notify(default!, default);
    }

    [Fact]
    public async Task No_token_in_context_is_reported_rather_than_ignored()
    {
        using var store = GetDocumentStore();

        Harness harness;
        using (var session = store.OpenAsyncSession())
        {
            harness = CreateAction(session, canManageOwner: true);
            await harness.Action.ExecuteAsync(ArgsFor(null));
        }

        harness.Client.ReceivedWithAnyArgs().Notify(default!, default);
    }
}
