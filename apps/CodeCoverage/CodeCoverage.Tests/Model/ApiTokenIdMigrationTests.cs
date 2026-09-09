using CodeCoverage.Entities;
using CodeCoverage.Migrations;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Xunit;

namespace CodeCoverage.Tests.Model;

/// <summary>
/// The migration that moves upload tokens off hash-shaped document ids.
/// </summary>
/// <remarks>
/// Its patch is an RQL string, and the migration deliberately names collections and fields as
/// strings so a later rename cannot break an already-applied migration — so nothing in the build
/// checks it. This does.
/// </remarks>
public class ApiTokenIdMigrationTests : CoverageRavenTest
{
    private const string Hash = "5460607aa7e061008c9c364705dacf50fa88dacebe96e41cc2c9e39b9055f30e";

    /// <summary>Writes a token the old way: hash as the id, no Hash field.</summary>
    private static async Task SeedLegacyAsync(IDocumentStore store, string hash, string description)
    {
        using var session = store.OpenAsyncSession();
        await session.StoreAsync(
            new ApiToken
            {
                Scope = "Account",
                AccountLogin = "MintPlayer",
                Description = description,
                CreatedAtUtc = new DateTime(2026, 8, 13, 10, 49, 57, DateTimeKind.Utc),
            },
            $"ApiTokens/{hash}");
        await session.SaveChangesAsync();

        // The entity now has a Hash property, which a legacy document did not — remove it so the
        // fixture is the real pre-migration shape rather than an approximation of it.
        var operation = await store.Operations.SendAsync(
            new Raven.Client.Documents.Operations.PatchByQueryOperation(
                new Raven.Client.Documents.Queries.IndexQuery
                {
                    Query = "from ApiTokens update { delete this.Hash; }",
                }));
        await operation.WaitForCompletionAsync(TimeSpan.FromMinutes(1));
    }

    private static Task RunAsync(IDocumentStore store)
        => new M_202609091300_ApiTokenIdIsNoLongerTheHash(store).UpAsync(CancellationToken.None);

    private static async Task<List<ApiToken>> AllAsync(IDocumentStore store)
    {
        using var session = store.OpenAsyncSession();
        return await session.Query<ApiToken>()
            .Customize(q => q.WaitForNonStaleResults())
            .ToListAsync();
    }

    [Fact]
    public async Task The_hash_moves_from_the_id_into_a_field()
    {
        using var store = GetDocumentStore();
        await SeedLegacyAsync(store, Hash, "MintPlayer token");

        await RunAsync(store);

        var tokens = await AllAsync(store);
        Assert.Single(tokens);
        Assert.Equal(Hash, tokens[0].Hash);
        Assert.DoesNotContain(Hash, tokens[0].Id);
    }

    [Fact]
    public async Task Every_other_field_survives()
    {
        using var store = GetDocumentStore();
        await SeedLegacyAsync(store, Hash, "MintPlayer token");

        await RunAsync(store);

        var token = (await AllAsync(store))[0];
        Assert.Equal("Account", token.Scope);
        Assert.Equal("MintPlayer", token.AccountLogin);
        Assert.Equal("MintPlayer token", token.Description);
        Assert.Equal(new DateTime(2026, 8, 13, 10, 49, 57, DateTimeKind.Utc), token.CreatedAtUtc.ToUniversalTime());
    }

    /// <summary>
    /// The old document must be gone, not merely superseded — leaving it would keep the hash in an
    /// id and double every token.
    /// </summary>
    [Fact]
    public async Task The_old_document_is_deleted()
    {
        using var store = GetDocumentStore();
        await SeedLegacyAsync(store, Hash, "MintPlayer token");

        await RunAsync(store);

        using var session = store.OpenAsyncSession();
        Assert.Null(await session.LoadAsync<ApiToken>($"ApiTokens/{Hash}"));
        Assert.Single(await AllAsync(store));
    }

    /// <summary>
    /// A replay must not duplicate anything, since the runner retries a migration whose marker was
    /// never written.
    /// </summary>
    [Fact]
    public async Task Replaying_it_changes_nothing()
    {
        using var store = GetDocumentStore();
        await SeedLegacyAsync(store, Hash, "MintPlayer token");

        await RunAsync(store);
        var first = (await AllAsync(store)).Single();

        await RunAsync(store);
        var second = (await AllAsync(store)).Single();

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.Hash, second.Hash);
    }

    /// <summary>
    /// A token written after the deploy already has a guid id and a hash, and must be left alone.
    /// </summary>
    [Fact]
    public async Task An_already_migrated_token_is_untouched()
    {
        using var store = GetDocumentStore();

        string id;
        using (var session = store.OpenAsyncSession())
        {
            var token = new ApiToken { Scope = "Account", Hash = "already", CreatedAtUtc = DateTime.UtcNow };
            await session.StoreAsync(token, ApiToken.NewDocumentId());
            await session.SaveChangesAsync();
            id = token.Id!;
        }

        await RunAsync(store);

        var tokens = await AllAsync(store);
        Assert.Single(tokens);
        Assert.Equal(id, tokens[0].Id);
    }
}
