using CodeCoverage.Tests._Infrastructure;
using System.Text.Json;
using CodeCoverage.Actions;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Exceptions;
using NSubstitute;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.ApiTokens;

/// <summary>
/// A token's owner is decided by ONE field, <see cref="ApiToken.Account"/> — the account document
/// id — and the caller never chooses it.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>This is a privilege-escalation regression test.</b> Token creation used to authorize on a
/// posted <c>AccountOwnerKey</c> and then resolve the account from a posted <c>AccountLogin</c> —
/// two client-writable fields — so a key you managed plus somebody else's login minted a token
/// stamped with <em>their</em> account. Both fields are gone (2026-10-02): on create the account is
/// the one the New was started from (<c>obj.Parent</c>, resolved by Spark), it is re-authorized on
/// every save, and it is read-only in the model so an edit keeps the stored value.
/// </para>
/// <para>
/// The attack of posting a foreign login is therefore structurally impossible; these tests pin the
/// guards that replaced it.
/// </para>
/// </remarks>
public class ApiTokenOwnerBindingTests : CoverageRavenTest
{
    private const string Mine = "acme";
    private const string Victim = "victim-org";

    private static readonly string MyAccount = Account.DocumentId(EForgeProvider.GitHub, 7);
    private static readonly string VictimAccount = Account.DocumentId(EForgeProvider.GitHub, 999);

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

    /// <summary>The token PO being saved, optionally started from a parent PO (what Spark resolves server-side).</summary>
    private static PersistentObject Po(string? parentId = null, string parentName = nameof(Account)) => new()
    {
        Name = "ApiToken",
        ObjectTypeId = Guid.NewGuid(),
        Parent = parentId is null ? null : new PersistentObject { Id = parentId, Name = parentName, ObjectTypeId = Guid.NewGuid() },
    };

    private static async Task SeedAsync(IDocumentStore store)
    {
        using var session = store.OpenAsyncSession();
        await session.StoreAsync(new Account { GitHubId = 7, Login = Mine, Provider = EForgeProvider.GitHub }, MyAccount);
        await session.StoreAsync(new Account { GitHubId = 999, Login = Victim, Provider = EForgeProvider.GitHub }, VictimAccount);
        await session.SaveChangesAsync();
    }

    /// <summary>The positive: a create under an account I manage is stamped with that account's document id.</summary>
    [Fact]
    public async Task A_token_created_under_an_account_I_manage_references_that_account()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        using var session = store.OpenAsyncSession();
        var entity = new ApiToken { Description = "ci" };

        await CreateActions(session, KeyFor(Mine)).BeforeSaveAsync(Po(MyAccount), entity);

        entity.Account.Should().Be(MyAccount);
        entity.Hash.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// A posted Account is never trusted on create: the parent decides, and a value smuggled onto
    /// the entity is overwritten.
    /// </summary>
    [Fact]
    public async Task A_posted_account_is_overwritten_by_the_parent_on_create()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        using var session = store.OpenAsyncSession();
        var entity = new ApiToken { Description = "ci", Account = VictimAccount };

        await CreateActions(session, KeyFor(Mine)).BeforeSaveAsync(Po(MyAccount), entity);

        entity.Account.Should().Be(MyAccount);
    }

    /// <summary>
    /// A create that was not started from an Account — no parent at all, a parent of another type,
    /// or a parent without an id — has no owner and is refused, even with an Account posted.
    /// </summary>
    [Theory]
    [InlineData(null, nameof(Account))]
    [InlineData("Repositories/github/10", "Repository")]
    [InlineData("", nameof(Account))]
    public async Task A_create_without_an_account_parent_is_refused(string? parentId, string parentName)
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        using var session = store.OpenAsyncSession();
        var entity = new ApiToken { Description = "ci", Account = MyAccount };

        var act = async () => await CreateActions(session, KeyFor(Mine)).BeforeSaveAsync(Po(parentId, parentName), entity);

        await act.Should().ThrowAsync<SparkValidationException>();
        entity.Hash.Should().BeEmpty();
    }

    /// <summary>The attack in its new shape: start the New from somebody else's account page.</summary>
    [Fact]
    public async Task A_create_under_an_account_I_do_not_manage_is_refused()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        using var session = store.OpenAsyncSession();
        var entity = new ApiToken { Description = "ci" };

        var act = async () => await CreateActions(session, KeyFor(Mine)).BeforeSaveAsync(Po(VictimAccount), entity);

        await act.Should().ThrowAsync<SparkValidationException>();
        entity.Hash.Should().BeEmpty();
    }

    /// <summary>A parent id that names no Account document is refused, not stamped.</summary>
    [Fact]
    public async Task A_create_under_a_missing_account_is_refused()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        using var session = store.OpenAsyncSession();

        var act = async () => await CreateActions(session, KeyFor(Mine))
            .BeforeSaveAsync(Po(Account.DocumentId(EForgeProvider.GitHub, 12345)), new ApiToken { Description = "ci" });

        await act.Should().ThrowAsync<SparkValidationException>();
    }

    /// <summary>
    /// ⚠️ Forge-qualified: a GitLab account of the same login and number is a different owner
    /// (<c>gitlab:acme</c>), so managing <c>github:acme</c> does not let a token be filed under it.
    /// </summary>
    [Fact]
    public async Task A_same_named_account_on_another_forge_is_not_mine()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        var gitlabTwin = Account.DocumentId(EForgeProvider.GitLab, 7);
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new Account { GitHubId = 7, Login = Mine, Provider = EForgeProvider.GitLab }, gitlabTwin);
            await seed.SaveChangesAsync();
        }
        using var session = store.OpenAsyncSession();

        var act = async () => await CreateActions(session, KeyFor(Mine)).BeforeSaveAsync(Po(gitlabTwin), new ApiToken { Description = "ci" });

        await act.Should().ThrowAsync<SparkValidationException>();
    }

    /// <summary>
    /// An edit keeps the stored account: the parent an edit happens to carry does not move it.
    /// </summary>
    [Fact]
    public async Task An_edit_ignores_the_parent_and_keeps_the_stored_account()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        using var session = store.OpenAsyncSession();
        var actions = CreateActions(session, KeyFor(Mine), KeyFor(Victim));
        var entity = new ApiToken { Description = "ci" };
        await actions.BeforeSaveAsync(Po(MyAccount), entity);
        entity.Hash.Should().NotBeNullOrEmpty(); // it really is an edit from here on

        await actions.BeforeSaveAsync(Po(VictimAccount), entity);

        entity.Account.Should().Be(MyAccount);
    }

    /// <summary>
    /// Re-authorized on every save: once the caller no longer manages the token's account, an edit
    /// is refused — and so is an edit whose Account somehow names an account the caller does not
    /// manage (defence in depth behind the read-only model attribute).
    /// </summary>
    [Fact]
    public async Task An_edit_of_a_token_whose_account_I_do_not_manage_is_refused()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        using var session = store.OpenAsyncSession();
        var entity = new ApiToken { Description = "ci" };
        await CreateActions(session, KeyFor(Mine)).BeforeSaveAsync(Po(MyAccount), entity);

        // Membership revoked since minting.
        var revoked = async () => await CreateActions(session, KeyFor("someone-else")).BeforeSaveAsync(Po(), entity);
        await revoked.Should().ThrowAsync<SparkValidationException>();

        // Account repointed at the victim.
        entity.Account = VictimAccount;
        var moved = async () => await CreateActions(session, KeyFor(Mine)).BeforeSaveAsync(Po(), entity);
        await moved.Should().ThrowAsync<SparkValidationException>();
    }

    /// <summary>
    /// The model is what makes an edit unable to move the account: Spark's EntityMapper refuses a
    /// posted value for an attribute the schema marks read-only. Likewise for the owner columns read
    /// from the account through ApiTokens_Overview.
    /// </summary>
    [Theory]
    [InlineData(nameof(ApiToken.Account))]
    [InlineData("Provider")]
    public void The_owner_attributes_are_read_only_in_the_model(string attribute)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "MintPlayer.Spark.slnx")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull();
        var path = Path.Combine(dir!, "apps", "CodeCoverage", "CodeCoverage", "App_Data", "Model", "ApiToken.json");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var definition = doc.RootElement.GetProperty("persistentObject").GetProperty("attributes").EnumerateArray()
            .Single(a => a.GetProperty("name").GetString() == attribute);

        definition.GetProperty("isReadOnly").GetBoolean().Should().BeTrue();
    }
}
