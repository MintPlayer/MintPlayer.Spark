using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;
using Xunit;
using Legacy = CodeCoverage.Migrations.M_202610021200_ApiTokenReferencesItsAccount.LegacyApiToken;
using Migration = CodeCoverage.Migrations.M_202610021200_ApiTokenReferencesItsAccount;

namespace CodeCoverage.Tests.Model;

/// <summary>
/// <c>M_202610021200_ApiTokenReferencesItsAccount</c>: every older token shape resolves to its
/// Account document id, and the four owner fields it replaces are removed from the document.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Migration.Resolve"/> is tested branch by branch, in the order of how much the token
/// proves; then the whole migration runs against an embedded RavenDB over documents written in the
/// legacy JSON shape — through a patch, because the entity can no longer produce that shape.
/// </para>
/// </remarks>
public class ApiTokenReferencesItsAccountMigrationTests : CoverageRavenTest
{
    private static readonly string GitHub7 = Account.DocumentId(EForgeProvider.GitHub, 7);
    private static readonly string GitLab7 = Account.DocumentId(EForgeProvider.GitLab, 7);
    private static readonly string GitHub9 = Account.DocumentId(EForgeProvider.GitHub, 9);

    private static readonly HashSet<string> Ids = new(StringComparer.OrdinalIgnoreCase) { GitHub7, GitLab7, GitHub9 };

    private static readonly Dictionary<string, string> ByOwnerKey = new(StringComparer.OrdinalIgnoreCase)
    {
        ["github:acme"] = GitHub7,
        ["gitlab:acme"] = GitLab7,
        ["github:other"] = GitHub9,
    };

    private static string? Resolve(Legacy token) => Migration.Resolve(token, Ids, ByOwnerKey);

    // ------------------------------------------------------------------------------------------
    // Resolve, branch by branch
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void An_existing_account_is_kept_when_that_account_exists()
    {
        // Wins over every other field, even ones that name a different account.
        Resolve(new Legacy { Account = GitHub9, AccountId = 7, Provider = "GitHub", AccountOwnerKey = "github:acme" })
            .Should().Be(GitHub9);
    }

    [Fact]
    public void An_existing_account_that_does_not_exist_falls_through_to_the_other_fields()
    {
        Resolve(new Legacy { Account = "Accounts/github/404", AccountOwnerKey = "github:acme" }).Should().Be(GitHub7);
        Resolve(new Legacy { Account = "Accounts/github/404" }).Should().BeNull();
    }

    [Theory]
    [InlineData("GitHub", "Accounts/github/7")]
    [InlineData("github", "Accounts/github/7")]   // parsed ignoring case
    [InlineData("GitLab", "Accounts/gitlab/7")]   // the provider decides the forge, not a default
    public void The_provider_and_numeric_id_name_the_document(string provider, string expected)
    {
        // The owner key names a different account: the id pair proves more and wins.
        Resolve(new Legacy { AccountId = 7, Provider = provider, AccountOwnerKey = "github:other" })
            .Should().Be(expected);
    }

    [Fact]
    public void A_numeric_id_whose_account_does_not_exist_falls_through_to_the_owner_key()
    {
        Resolve(new Legacy { AccountId = 404, Provider = "GitHub", AccountOwnerKey = "github:other" }).Should().Be(GitHub9);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NoSuchForge")]
    public void A_numeric_id_without_a_parseable_provider_is_not_guessed(string? provider)
    {
        // No default to GitHub: an unparseable provider skips the pair entirely.
        Resolve(new Legacy { AccountId = 7, Provider = provider }).Should().BeNull();
        Resolve(new Legacy { AccountId = 7, Provider = provider, AccountOwnerKey = "gitlab:acme" }).Should().Be(GitLab7);
    }

    [Fact]
    public void The_owner_key_is_looked_up_when_there_is_no_id_pair()
    {
        Resolve(new Legacy { AccountOwnerKey = "gitlab:acme", AccountLogin = "other" }).Should().Be(GitLab7);
        Resolve(new Legacy { AccountOwnerKey = "github:nobody" }).Should().BeNull();
    }

    [Fact]
    public void A_bare_login_is_read_as_a_GitHub_login()
    {
        Resolve(new Legacy { AccountLogin = "acme" }).Should().Be(GitHub7);
        Resolve(new Legacy { AccountLogin = "ACME" }).Should().Be(GitHub7);
        Resolve(new Legacy { AccountLogin = "nobody" }).Should().BeNull();
    }

    [Fact]
    public void A_token_that_names_nothing_resolves_to_null()
    {
        Resolve(new Legacy()).Should().BeNull();
        Resolve(new Legacy { Account = "", AccountOwnerKey = "", AccountLogin = "" }).Should().BeNull();
    }

    // ------------------------------------------------------------------------------------------
    // The whole migration, against legacy-shaped documents
    // ------------------------------------------------------------------------------------------

    /// <summary>Every field of the raw document, including the four the migration must remove.</summary>
    private sealed class RawToken
    {
        public string? Account { get; set; }
        public string? AccountLogin { get; set; }
        public string? AccountOwnerKey { get; set; }
        public long? AccountId { get; set; }
        public string? Provider { get; set; }
        public string? Description { get; set; }
        public string? Hash { get; set; }
    }

    private static Task RunAsync(IDocumentStore store)
        => new Migration(store, NullLogger<Migration>.Instance).UpAsync(CancellationToken.None);

    /// <summary>
    /// Stores a token, then patches the legacy owner fields onto it — the entity no longer declares
    /// them, so only a patch can write the shape production holds.
    /// </summary>
    private static async Task SeedLegacyAsync(IDocumentStore store, string id, string legacyFields)
    {
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new ApiToken { Description = id, Hash = "h-" + id, Scope = "Account" }, id);
            await seed.SaveChangesAsync();
        }

        var patch = await store.Operations.SendAsync(new PatchByQueryOperation(new IndexQuery
        {
            Query = $"from ApiTokens as d where id(d) = '{id}' update {{ delete d.Account; {legacyFields} }}",
        }));
        await patch.WaitForCompletionAsync(TimeSpan.FromSeconds(30));
    }

    private static async Task SeedAccountsAsync(IDocumentStore store)
    {
        using var seed = store.OpenAsyncSession();
        await seed.StoreAsync(new Account { GitHubId = 7, Login = "acme" }, GitHub7);
        await seed.StoreAsync(new Account { GitHubId = 7, Login = "acme", Provider = EForgeProvider.GitLab }, GitLab7);
        await seed.StoreAsync(new Account { GitHubId = 9, Login = "other" }, GitHub9);
        await seed.SaveChangesAsync();
    }

    /// <summary>The names of the top-level fields the stored document actually has.</summary>
    private static async Task<HashSet<string>> RawFieldNamesAsync(IDocumentStore store, string id)
    {
        using var session = store.OpenAsyncSession();
        var raw = await session.LoadAsync<Dictionary<string, object?>>(id);
        return [.. raw.Keys];
    }

    [Fact]
    public async Task Every_legacy_shape_gets_its_account_and_loses_the_four_owner_fields()
    {
        using var store = GetDocumentStore();
        await SeedAccountsAsync(store);
        await SeedLegacyAsync(store, "ApiTokens/pair",
            "d.AccountId = 7; d.Provider = 'GitLab'; d.AccountOwnerKey = 'github:other'; d.AccountLogin = 'other';");
        await SeedLegacyAsync(store, "ApiTokens/key", "d.AccountOwnerKey = 'github:other'; d.AccountLogin = 'other';");
        await SeedLegacyAsync(store, "ApiTokens/login", "d.AccountLogin = 'acme';");
        await SeedLegacyAsync(store, "ApiTokens/unknown", "d.AccountLogin = 'nobody'; d.AccountId = 404; d.Provider = 'GitHub';");

        await RunAsync(store);

        using (var verify = store.OpenAsyncSession())
        {
            (await verify.LoadAsync<ApiToken>("ApiTokens/pair")).Account.Should().Be(GitLab7);
            (await verify.LoadAsync<ApiToken>("ApiTokens/key")).Account.Should().Be(GitHub9);
            (await verify.LoadAsync<ApiToken>("ApiTokens/login")).Account.Should().Be(GitHub7);
            // Unresolvable: fails closed, no account.
            (await verify.LoadAsync<ApiToken>("ApiTokens/unknown")).Account.Should().BeNull();
        }

        foreach (var id in new[] { "ApiTokens/pair", "ApiTokens/key", "ApiTokens/login", "ApiTokens/unknown" })
        {
            var fields = await RawFieldNamesAsync(store, id);
            // ⚠️ Gone from the DOCUMENT, not just from the class: RavenDB keeps undeclared fields.
            fields.Should().NotContain(nameof(RawToken.AccountLogin));
            fields.Should().NotContain(nameof(RawToken.AccountOwnerKey));
            fields.Should().NotContain(nameof(RawToken.AccountId));
            fields.Should().NotContain(nameof(RawToken.Provider));
            fields.Should().Contain(nameof(RawToken.Account));
        }
    }

    [Fact]
    public async Task The_other_fields_of_a_token_survive()
    {
        using var store = GetDocumentStore();
        await SeedAccountsAsync(store);
        await SeedLegacyAsync(store, "ApiTokens/x", "d.AccountLogin = 'acme';");

        await RunAsync(store);

        using var verify = store.OpenAsyncSession();
        var token = await verify.LoadAsync<RawToken>("ApiTokens/x");
        token.Hash.Should().Be("h-ApiTokens/x");
        token.Description.Should().Be("ApiTokens/x");
    }

    /// <summary>An already-migrated token — Account set, no legacy fields — is left as it is.</summary>
    [Fact]
    public async Task Running_the_migration_twice_changes_nothing()
    {
        using var store = GetDocumentStore();
        await SeedAccountsAsync(store);
        await SeedLegacyAsync(store, "ApiTokens/pair", "d.AccountId = 7; d.Provider = 'GitHub';");

        await RunAsync(store);
        await RunAsync(store);

        using var verify = store.OpenAsyncSession();
        (await verify.LoadAsync<ApiToken>("ApiTokens/pair")).Account.Should().Be(GitHub7);
    }
}
