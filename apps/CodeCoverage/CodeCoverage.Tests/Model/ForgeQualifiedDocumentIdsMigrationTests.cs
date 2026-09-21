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
    /// The guard that stands between a failed put phase and permanent data loss. It is driven by
    /// counts, and those counts are answered from the same auto-index the delete uses — so if they
    /// ever come back stale the guard is deciding on a picture of the database taken before the
    /// migration started. Rather than risk that, the count throws — and throwing is safe, because
    /// no delete runs before its collection's replacements are already written.
    /// </summary>
    [Fact]
    public async Task A_count_that_cannot_catch_up_refuses_rather_than_guesses()
    {
        using var store = GetDocumentStore();
        await SeedLegacyAsync(store);

        var original = M_202609210900_ForgeQualifiedDocumentIds.IndexCatchUpBudget;
        try
        {
            // Not "a short wait" — zero. Anything larger is a race with a fast machine.
            M_202609210900_ForgeQualifiedDocumentIds.IndexCatchUpBudget = TimeSpan.Zero;

            var act = async () => await RunAsync(store);
            await act.Should().ThrowAsync<Exception>();
        }
        finally
        {
            M_202609210900_ForgeQualifiedDocumentIds.IndexCatchUpBudget = original;
        }

        // ⚠ The recoverable state is NOT "nothing happened". Phases run per collection, so a
        // throw part-way leaves earlier collections fully migrated and later ones untouched. What
        // must hold is that nothing was DESTROYED: the put phase runs before any delete, so every
        // document that could have been deleted already has its replacement. Re-running finishes
        // the job, which is why the applied-marker is written only after Up returns.
        (await ExistsAsync(store, $"Repositories/github/{RepositoryId}")).Should().BeTrue();
    }
}
