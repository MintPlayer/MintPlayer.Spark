using CodeCoverage.Actions;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using NSubstitute;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.ApiTokens;

/// <summary>
/// A token's owner is decided by ONE field, and everything else is derived from it.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>This is a privilege-escalation regression test.</b> Token creation authorized on
/// <c>AccountOwnerKey</c> and then resolved the account from <c>AccountLogin</c> — a second,
/// independently client-writable field that nothing validated and nothing server-assigned. Posting
/// a key you genuinely manage together with somebody else's login minted a token stamped with
/// <em>their</em> account.
/// </para>
/// <para>
/// It was invisible to the row filter, which compares the key — the field the attacker sets
/// honestly — so the token stayed visible and editable by its creator while
/// <c>UploadsController</c> authorized its uploads against the victim's repositories. Where no
/// account matched the posted login it was worse: <c>AccountId</c> stayed null, and the legacy
/// login-comparison arm authorized every repository owned by that name.
/// </para>
/// </remarks>
public class ApiTokenOwnerBindingTests : CoverageRavenTest
{
    private const string Mine = "acme";
    private const string Victim = "victim-org";

    private static string KeyFor(string login, EForgeProvider provider = EForgeProvider.GitHub)
        => new ForgeOwner(provider, login).ToString();

    private static ApiTokenActions CreateActions(IAsyncDocumentSession session, params string[] ownerKeys)
    {
        var visibility = Substitute.For<ISparkVisibility>();
        visibility.GetAllowedOwnersAsync().Returns(Task.FromResult(ownerKeys));
        visibility.CanManageOwnerAsync(Arg.Any<string>())
            .Returns(call => Task.FromResult(ownerKeys.Contains(call.Arg<string>(), StringComparer.OrdinalIgnoreCase)));

        var ctor = typeof(ApiTokenActions).GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length).First();
        var args = ctor.GetParameters()
            .Select(object? (p) =>
            {
                if (p.ParameterType == typeof(ISparkVisibility)) return visibility;
                if (p.ParameterType == typeof(IAsyncDocumentSession)) return session;
                // ⚠️ HttpContext must be explicitly null. NSubstitute auto-substitutes it otherwise,
                // which makes `principal is null` false and sends the create path into UserManager —
                // a concrete class that cannot be proxied. Same reasoning as ApiTokenRepositoryScopeTests.
                if (p.ParameterType == typeof(Microsoft.AspNetCore.Http.IHttpContextAccessor))
                {
                    var accessor = Substitute.For<Microsoft.AspNetCore.Http.IHttpContextAccessor>();
                    accessor.HttpContext.Returns((Microsoft.AspNetCore.Http.HttpContext?)null);
                    return accessor;
                }
                try { return Substitute.For([p.ParameterType], null); }
                catch { return null; }
            })
            .ToArray();
        return (ApiTokenActions)ctor.Invoke(args);
    }

    private static PersistentObject Po() => new() { Name = "ApiToken", ObjectTypeId = Guid.NewGuid() };

    private static async Task SeedVictimAccountAsync(IDocumentStore store, long id = 999)
    {
        using var session = store.OpenAsyncSession();
        await session.StoreAsync(
            new Account { GitHubId = id, Login = Victim, Provider = EForgeProvider.GitHub },
            Account.DocumentId(EForgeProvider.GitHub, id));
        await session.SaveChangesAsync();
    }

    /// <summary>
    /// The attack: authorize on a key I manage, name somebody else in the login field.
    /// </summary>
    [Fact]
    public async Task A_posted_login_cannot_steal_another_accounts_identity()
    {
        using var store = GetDocumentStore();
        await SeedVictimAccountAsync(store);

        using var session = store.OpenAsyncSession();
        var entity = new ApiToken
        {
            Description = "ci",
            AccountOwnerKey = KeyFor(Mine),   // genuinely mine — passes the check
            AccountLogin = Victim,            // somebody else's
        };

        await CreateActions(session, KeyFor(Mine)).OnBeforeSaveAsync(Po(), entity);

        // The victim's numeric id must NOT have been stamped on my token.
        Assert.NotEqual(999, entity.AccountId);
        // And the login is overwritten with the one the authorized key names.
        Assert.Equal(Mine, entity.AccountLogin);
    }

    /// <summary>
    /// The nastier half: with no matching account the id stays null, and the upload path's legacy
    /// arm then authorizes on the login claim — so a stolen login is worse than a stolen id.
    /// </summary>
    [Fact]
    public async Task A_posted_login_for_an_unknown_account_cannot_survive_either()
    {
        using var store = GetDocumentStore();
        // No Account document for the victim at all.

        using var session = store.OpenAsyncSession();
        var entity = new ApiToken
        {
            Description = "ci",
            AccountOwnerKey = KeyFor(Mine),
            AccountLogin = "some-org-i-do-not-manage",
        };

        await CreateActions(session, KeyFor(Mine)).OnBeforeSaveAsync(Po(), entity);

        Assert.Equal(Mine, entity.AccountLogin);
    }

    /// <summary>
    /// ⚠️ The account lookup must match on provider AND login. A bare login match unions forges, so
    /// a GitLab group of the same name would have supplied the numeric id.
    /// </summary>
    [Fact]
    public async Task A_same_named_account_on_another_forge_does_not_supply_the_id()
    {
        using var store = GetDocumentStore();
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(
                new Account { GitHubId = 555, Login = Mine, Provider = EForgeProvider.GitLab },
                Account.DocumentId(EForgeProvider.GitLab, 555));
            await seed.SaveChangesAsync();
        }

        using var session = store.OpenAsyncSession();
        var entity = new ApiToken { Description = "ci", AccountOwnerKey = KeyFor(Mine), AccountLogin = Mine };

        await CreateActions(session, KeyFor(Mine)).OnBeforeSaveAsync(Po(), entity);

        Assert.NotEqual(555, entity.AccountId);
        Assert.Equal(EForgeProvider.GitHub, entity.Provider);
    }

    /// <summary>
    /// The paired positive: a token for an account I manage IS stamped, or the tests above would
    /// pass against a version that simply never stamps anything.
    /// </summary>
    [Fact]
    public async Task A_token_for_an_account_I_manage_is_stamped_from_the_key()
    {
        using var store = GetDocumentStore();
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(
                new Account { GitHubId = 7, Login = Mine, Provider = EForgeProvider.GitHub },
                Account.DocumentId(EForgeProvider.GitHub, 7));
            await seed.SaveChangesAsync();
        }

        using var session = store.OpenAsyncSession();
        var entity = new ApiToken { Description = "ci", AccountOwnerKey = KeyFor(Mine), AccountLogin = Mine };

        await CreateActions(session, KeyFor(Mine)).OnBeforeSaveAsync(Po(), entity);

        Assert.Equal(7, entity.AccountId);
        Assert.Equal(EForgeProvider.GitHub, entity.Provider);
        Assert.Equal(Mine, entity.AccountLogin);
    }

    [Fact]
    public async Task An_unparseable_owner_key_is_refused()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        var entity = new ApiToken { Description = "ci", AccountOwnerKey = "not-a-key", AccountLogin = Mine };

        await Assert.ThrowsAsync<SparkValidationException>(
            () => CreateActions(session, "not-a-key").OnBeforeSaveAsync(Po(), entity));
    }
}
