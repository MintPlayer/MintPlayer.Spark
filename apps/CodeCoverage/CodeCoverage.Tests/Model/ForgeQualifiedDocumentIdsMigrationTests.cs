using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Xunit;

namespace CodeCoverage.Tests.Model;

/// <summary>
/// The re-key that gives every document id the forge that hosts it.
/// </summary>
/// <remarks>
/// <para>
/// This is the riskiest thing in the multi-forge work: it rewrites ~224,000 production documents,
/// RavenDB ids are immutable so it is a put-then-delete rather than a rename, and its scripts are
/// RQL strings that nothing in the build type-checks.
/// </para>
/// <para>
/// ⚠️ <b>It ran against a rehearsal copy and still shipped a bug that broke it on a 238-document
/// database.</b> The delete phase queries by id prefix, RavenDB answers that from an auto-index,
/// and the put phase immediately before it had just rewritten every document — so the index was
/// stale and <c>DeleteByQueryOperation</c> refused outright with "Cannot perform bulk operation.
/// Index is stale." Because the applied-marker is only written after <c>Up</c> returns, the
/// container would have restarted and failed the same way forever. A rehearsal that passes is not
/// the same as a test that runs, which is what this is.
/// </para>
/// </remarks>
public class ForgeQualifiedDocumentIdsMigrationTests : CoverageRavenTest
{
    private const long RepositoryId = 402741072;
    private const string Sha = "79bc284939350991803acc84ced894ade844b9f0";

    /// <summary>
    /// Seeds the pre-migration shape: unqualified ids, and references spelled the old way.
    /// </summary>
    private static async Task SeedLegacyAsync(IDocumentStore store)
    {
        using var session = store.OpenAsyncSession();

        await session.StoreAsync(
            new Account { GitHubId = 7, Login = "MintPlayer", Type = "Organization" },
            "Accounts/7");

        await session.StoreAsync(
            new Repository
            {
                GitHubId = RepositoryId,
                Name = "MintPlayer.Spark",
                FullName = "MintPlayer/MintPlayer.Spark",
                OwnerLogin = "MintPlayer",
                Account = "Accounts/7",
                DefaultBranch = "master",
            },
            $"Repositories/{RepositoryId}");

        await session.StoreAsync(
            new Commit
            {
                Sha = Sha,
                Repository = $"Repositories/{RepositoryId}",
                Branch = "master",
            },
            $"Commits/{RepositoryId}/{Sha}");

        await session.SaveChangesAsync();
    }

    private static Task RunAsync(IDocumentStore store)
        => new M_202609210900_ForgeQualifiedDocumentIds(
            store,
            NullLogger<M_202609210900_ForgeQualifiedDocumentIds>.Instance)
        .UpAsync(CancellationToken.None);

    private static async Task<bool> ExistsAsync(IDocumentStore store, string id)
    {
        using var session = store.OpenAsyncSession();
        return await session.Advanced.ExistsAsync(id);
    }

    /// <summary>
    /// The whole migration, back to back, exactly as a container start runs it — no waiting for
    /// indexing in between. That gap is where the staleness bug lived, so introducing one here
    /// would make this test pass against the broken version.
    /// </summary>
    [Fact]
    public async Task Every_id_gains_the_forge_and_the_legacy_documents_go()
    {
        using var store = GetDocumentStore();
        await SeedLegacyAsync(store);

        await RunAsync(store);

        (await ExistsAsync(store, $"Repositories/github/{RepositoryId}")).Should().BeTrue();
        (await ExistsAsync(store, "Accounts/github/7")).Should().BeTrue();
        (await ExistsAsync(store, $"Commits/github/{RepositoryId}/{Sha}")).Should().BeTrue();

        (await ExistsAsync(store, $"Repositories/{RepositoryId}")).Should().BeFalse();
        (await ExistsAsync(store, "Accounts/7")).Should().BeFalse();
        (await ExistsAsync(store, $"Commits/{RepositoryId}/{Sha}")).Should().BeFalse();
    }

    /// <summary>
    /// A reference that still points at an unqualified id is a dangling reference, and the page
    /// that follows it renders empty rather than erroring — which is why the rewrite rides along
    /// inside the put script instead of being a later pass that could be skipped.
    /// </summary>
    [Fact]
    public async Task Reference_fields_are_rewritten_to_the_new_ids()
    {
        using var store = GetDocumentStore();
        await SeedLegacyAsync(store);

        await RunAsync(store);

        using var session = store.OpenAsyncSession();
        var repository = await session.LoadAsync<Repository>($"Repositories/github/{RepositoryId}");
        var commit = await session.LoadAsync<Commit>($"Commits/github/{RepositoryId}/{Sha}");

        repository.Account.Should().Be("Accounts/github/7");
        commit.Repository.Should().Be($"Repositories/github/{RepositoryId}");
    }

    /// <summary>
    /// The fields the authorization filters compare. Backfilling them is not cosmetic: an owner
    /// set of bare logins unions forges, so an empty OwnerKey would compare equal to nothing and
    /// silently hide the repository from the people who manage it.
    /// </summary>
    [Fact]
    public async Task The_owner_key_and_provider_are_backfilled()
    {
        using var store = GetDocumentStore();
        await SeedLegacyAsync(store);

        await RunAsync(store);

        using var session = store.OpenAsyncSession();
        var repository = await session.LoadAsync<Repository>($"Repositories/github/{RepositoryId}");
        var account = await session.LoadAsync<Account>("Accounts/github/7");

        repository.Provider.Should().Be(EForgeProvider.GitHub);
        repository.OwnerKey.Should().Be("github:MintPlayer");
        account.Provider.Should().Be(EForgeProvider.GitHub);
    }

    /// <summary>
    /// Re-entrancy is a correctness property, not a nicety: the applied-marker is written only
    /// after <c>Up</c> returns, so any failure means the next container start runs this again from
    /// the top. A second pass must find nothing to do rather than re-qualify an already-qualified
    /// id into <c>Repositories/github/github/…</c>.
    /// </summary>
    [Fact]
    public async Task Running_it_twice_changes_nothing_the_second_time()
    {
        using var store = GetDocumentStore();
        await SeedLegacyAsync(store);

        await RunAsync(store);
        await RunAsync(store);

        (await ExistsAsync(store, $"Repositories/github/{RepositoryId}")).Should().BeTrue();
        (await ExistsAsync(store, $"Repositories/github/github/{RepositoryId}")).Should().BeFalse();

        WaitForIndexing(store);
        using var session = store.OpenAsyncSession();
        var all = await session.Advanced
            .AsyncRawQuery<object>("from Repositories")
            .ToListAsync(CancellationToken.None);
        all.Should().HaveCount(1);
    }

    /// <summary>
    /// The guard that stands between a failed put phase and permanent data loss is driven by counts, and
    /// those counts, like every other phase, read no index: <c>startsWith(id(d), $p)</c> alone is a collection
    /// query, and the delete tests its second condition in the patch script (D-O22). A count can therefore
    /// never answer from a stale picture, and the migration finishes even with no time at all to wait for
    /// an index.
    /// </summary>
    /// <remarks>
    /// This test used to assert the opposite, a throw with a zero budget. That throw came from the delete:
    /// its <c>and not startsWith(id(d), ...)</c> built <c>Auto/&lt;collection&gt;/ById()</c>, stale by definition
    /// when new, on every fresh database.
    /// </remarks>
    [Fact]
    public async Task No_phase_waits_on_an_index_so_a_zero_budget_still_completes()
    {
        using var store = GetDocumentStore();
        await SeedLegacyAsync(store);

        var original = M_202609210900_ForgeQualifiedDocumentIds.IndexCatchUpBudget;
        try
        {
            M_202609210900_ForgeQualifiedDocumentIds.IndexCatchUpBudget = TimeSpan.Zero;
            await RunAsync(store);
        }
        finally
        {
            M_202609210900_ForgeQualifiedDocumentIds.IndexCatchUpBudget = original;
        }

        (await ExistsAsync(store, $"Repositories/github/{RepositoryId}")).Should().BeTrue();
        (await ExistsAsync(store, $"Repositories/{RepositoryId}")).Should().BeFalse("the legacy copy is deleted once its replacement exists");
        (await store.Maintenance.SendAsync(new Raven.Client.Documents.Operations.Indexes.GetIndexNamesOperation(0, 1024)))
            .Where(name => name.StartsWith("Auto/", StringComparison.OrdinalIgnoreCase))
            .Should().BeEmpty("indexes belong to the application");
    }
}
