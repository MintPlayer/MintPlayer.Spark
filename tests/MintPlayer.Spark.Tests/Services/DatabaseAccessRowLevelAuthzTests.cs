using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Exercises the H-2/H-3 row-level authorization path through <see cref="IDatabaseAccess"/>:
/// <see cref="IDatabaseAccess.GetPersistentObjectAsync"/> returns null for denied rows (surfaces
/// as 404 at the endpoint, per M-3); <see cref="IDatabaseAccess.GetPersistentObjectsAsync"/>
/// filters denied rows out of the list result. The row-level filter calls into
/// <c>DefaultPersistentObjectActions{T}.IsAllowedAsync</c>, discovered by convention through
/// <see cref="ActionsResolver"/> — see <see cref="GuardedDocActions"/> in _Infrastructure.
/// </summary>
public class DatabaseAccessRowLevelAuthzTests : SparkTestDriver
{
    private static readonly Guid DocTypeId = Guid.Parse("7b0a11aa-11aa-11aa-11aa-7b0a11aa11aa");

    private SparkEndpointFactory<GuardedContext> _factory = null!;
    private IDatabaseAccess _dbAccess = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _factory = new SparkEndpointFactory<GuardedContext>(Store, new[] { GuardedDocModel.For(DocTypeId) });
        _dbAccess = _factory.GetService<IDatabaseAccess>();
    }

    public override async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private async Task SeedAsync(params GuardedDoc[] docs)
    {
        using (var session = Store.OpenAsyncSession())
        {
            foreach (var d in docs) await session.StoreAsync(d);
            await session.SaveChangesAsync();
        }
        await RavenIndexHelper.WaitForNonStaleAsync(Store);
    }

    [Fact]
    public async Task Get_returns_null_when_IsAllowedAsync_denies_the_row()
    {
        await SeedAsync(new GuardedDoc { Id = "docs/forbidden", Name = "secret", IsVisible = false });

        var result = await _dbAccess.GetPersistentObjectAsync(DocTypeId, "docs/forbidden");

        result.Should().BeNull();
    }

    [Fact]
    public async Task Get_returns_object_when_IsAllowedAsync_allows_the_row()
    {
        await SeedAsync(new GuardedDoc { Id = "docs/visible", Name = "public", IsVisible = true });

        var result = await _dbAccess.GetPersistentObjectAsync(DocTypeId, "docs/visible");

        result.Should().NotBeNull();
        result!.Id.Should().Be("docs/visible");
    }

    [Fact]
    public async Task GetAll_filters_denied_rows_out_of_the_list()
    {
        await SeedAsync(
            new GuardedDoc { Id = "docs/a", Name = "A", IsVisible = true },
            new GuardedDoc { Id = "docs/b", Name = "B", IsVisible = false },
            new GuardedDoc { Id = "docs/c", Name = "C", IsVisible = true });

        var results = (await _dbAccess.GetPersistentObjectsAsync(DocTypeId)).ToList();

        results.Should().HaveCount(2);
        results.Select(p => p.Id).Should().BeEquivalentTo(new[] { "docs/a", "docs/c" });
    }

    [Fact]
    public async Task GetAll_returns_empty_when_every_row_is_denied()
    {
        await SeedAsync(
            new GuardedDoc { Id = "docs/x", Name = "X", IsVisible = false },
            new GuardedDoc { Id = "docs/y", Name = "Y", IsVisible = false });

        var results = await _dbAccess.GetPersistentObjectsAsync(DocTypeId);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAll_returns_empty_when_collection_has_no_rows()
    {
        var results = await _dbAccess.GetPersistentObjectsAsync(DocTypeId);

        results.Should().BeEmpty();
    }
}
