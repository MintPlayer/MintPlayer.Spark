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
/// The stored <c>DisconnectedReason</c> values stop naming GitHub.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>Every fixture writes the OLD value through a patch, not through the entity.</b> The
/// constants have been renamed, so a C# fixture saying
/// <c>DisconnectedReasons.IntegrationRemoved</c> would write the <em>new</em> string and every
/// assertion below would pass against a migration that did nothing at all. That is the same trap
/// <see cref="ApiTokenForgeNeutralRenameTests"/> documents, and it is why these seeds are patches.
/// </para>
/// <para>
/// The value is a plain <c>const string</c> rather than an enum member, which is exactly why this
/// needed a migration: renaming the constant changes what the code compares, never what the
/// documents hold, and nothing branches on the reason — so a missed rename would show up only as a
/// word on a page, for ever.
/// </para>
/// </remarks>
public class DisconnectedReasonRenameTests : CoverageRavenTest
{
    private const long RepoId = 5150;

    private static Task RunAsync(IDocumentStore store)
        => new M_202609230900_DisconnectedReasonsStopNamingGitHub(
            store, NullLogger<M_202609230900_DisconnectedReasonsStopNamingGitHub>.Instance)
        .UpAsync(CancellationToken.None);

    /// <summary>Stores a repository, then writes the pre-rename reason straight onto the document.</summary>
    private static async Task SeedAsync(IDocumentStore store, string storedReason)
    {
        var id = Repository.DocumentId(EForgeProvider.GitHub, RepoId);

        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new Repository
            {
                GitHubId = RepoId,
                Name = "widgets",
                FullName = "acme/widgets",
                OwnerLogin = "acme",
                Connection = RepositoryConnection.Disconnected,
            }, id);
            await seed.SaveChangesAsync();
        }

        var patch = store.Operations.Send(new PatchByQueryOperation(new IndexQuery
        {
            Query = $"from Repositories as d where id(d) = '{id}' update {{ d.DisconnectedReason = '{storedReason}'; }}",
        }));
        patch.WaitForCompletion<BulkOperationResult>(TimeSpan.FromSeconds(30));
    }

    private static async Task<string?> ReasonAsync(IDocumentStore store)
    {
        using var verify = store.OpenAsyncSession();
        var repository = await verify.LoadAsync<Repository>(Repository.DocumentId(EForgeProvider.GitHub, RepoId));
        return repository.DisconnectedReason;
    }

    [Theory]
    [InlineData("AppUninstalled", "IntegrationRemoved")]
    [InlineData("AppSuspended", "IntegrationSuspended")]
    [InlineData("DeletedOnGitHub", "DeletedOnForge")]
    public async Task A_GitHub_named_reason_becomes_a_forge_neutral_one(string stored, string expected)
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, stored);

        await RunAsync(store);

        Assert.Equal(expected, await ReasonAsync(store));
    }

    /// <summary>
    /// A reason that never named GitHub is left exactly as it was — the rename must not be a
    /// blanket rewrite of the field.
    /// </summary>
    [Fact]
    public async Task A_neutral_reason_is_untouched()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, DisconnectedReasons.RemovedFromInstallation);

        await RunAsync(store);

        Assert.Equal(DisconnectedReasons.RemovedFromInstallation, await ReasonAsync(store));
    }

    /// <summary>
    /// ⚠️ Re-running must change nothing. Each statement matches the OLD value, so the second pass
    /// finds nothing — but a future edit that inverted a condition would turn this migration into
    /// one that rewrites on every startup, which a single-run test cannot see.
    /// </summary>
    [Fact]
    public async Task Running_it_twice_changes_nothing()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, "AppUninstalled");

        await RunAsync(store);
        await RunAsync(store);

        Assert.Equal(DisconnectedReasons.IntegrationRemoved, await ReasonAsync(store));
    }

    /// <summary>
    /// A connected repository carries no reason, and must not acquire one.
    /// </summary>
    [Fact]
    public async Task A_connected_repository_gains_no_reason()
    {
        using var store = GetDocumentStore();
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new Repository
            {
                GitHubId = RepoId,
                Name = "widgets",
                FullName = "acme/widgets",
                OwnerLogin = "acme",
                Connection = RepositoryConnection.Connected,
            }, Repository.DocumentId(EForgeProvider.GitHub, RepoId));
            await seed.SaveChangesAsync();
        }

        await RunAsync(store);

        Assert.Null(await ReasonAsync(store));
    }

    /// <summary>
    /// ⚠️ The rename covers all three collections that carry the field, not just repositories.
    /// Accounts were given the field only two migrations ago, so "it is a repository concern" was
    /// true recently enough to be worth pinning.
    /// </summary>
    [Fact]
    public async Task Accounts_are_renamed_too()
    {
        using var store = GetDocumentStore();
        var id = Account.DocumentId(EForgeProvider.GitHub, 4242);

        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new Account
            {
                GitHubId = 4242,
                Login = "acme",
                Provider = EForgeProvider.GitHub,
                Connection = RepositoryConnection.Disconnected,
            }, id);
            await seed.SaveChangesAsync();
        }

        var patch = store.Operations.Send(new PatchByQueryOperation(new IndexQuery
        {
            Query = $"from Accounts as d where id(d) = '{id}' update {{ d.DisconnectedReason = 'AppUninstalled'; }}",
        }));
        patch.WaitForCompletion<BulkOperationResult>(TimeSpan.FromSeconds(30));

        await RunAsync(store);

        using var verify = store.OpenAsyncSession();
        var account = await verify.LoadAsync<Account>(id);
        Assert.Equal(DisconnectedReasons.IntegrationRemoved, account.DisconnectedReason);
    }
}
