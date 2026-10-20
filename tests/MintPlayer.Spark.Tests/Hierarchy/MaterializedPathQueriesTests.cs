using MintPlayer.Spark.Hierarchy;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Hierarchy;

/// <summary>
/// Pins <see cref="MaterializedPathQueries"/> (WX9): the subtree prefix query and the subtree move, against a
/// real RavenDB, with every value a parameter.
/// </summary>
public class MaterializedPathQueriesTests : SparkTestDriver
{
    // Fixture names start with "Mp": ActionsResolver matches by simple name across the whole assembly.
    private sealed class MpLocation
    {
        public string? Id { get; set; }
        public string? Path { get; set; }
    }

    private async Task SeedAsync(params string[] paths)
    {
        using var session = Store.OpenAsyncSession();
        foreach (var path in paths)
            await session.StoreAsync(new MpLocation { Path = path }, "mplocations/" + path.Replace('/', '-'));
        await session.SaveChangesAsync();
    }

    private async Task<List<string>> PathsAsync()
    {
        using var session = Store.OpenAsyncSession();
        var all = await session.Query<MpLocation>().Customize(c => c.WaitForNonStaleResults()).ToListAsync();
        return all.Select(l => l.Path!).OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    [Fact]
    public async Task WhereDescendantOf_returns_the_subtree_and_not_a_prefix_sharing_sibling()
    {
        await SeedAsync("BRU", "BRU/A", "BRU/A/01", "BRU/A/01/03", "BRU/A10", "BRU/A10/01", "GNT/A");

        using var session = Store.OpenAsyncSession();
        var below = await session.Query<MpLocation>()
            .Customize(c => c.WaitForNonStaleResults())
            .WhereDescendantOf(l => l.Path, "BRU/A")
            .ToListAsync();

        below.Select(l => l.Path).Should().BeEquivalentTo(["BRU/A/01", "BRU/A/01/03"]);
    }

    [Fact]
    public async Task WhereDescendantOf_with_self_includes_the_ancestor()
    {
        await SeedAsync("BRU", "BRU/A", "BRU/A/01", "BRU/A10");

        using var session = Store.OpenAsyncSession();
        var below = await session.Query<MpLocation>()
            .Customize(c => c.WaitForNonStaleResults())
            .WhereDescendantOf(l => l.Path, "BRU/A", includeSelf: true)
            .ToListAsync();

        below.Select(l => l.Path).Should().BeEquivalentTo(["BRU/A", "BRU/A/01"]);
    }

    /// <summary>The value travels as a parameter: the query text never holds the prefix.</summary>
    [Fact]
    public void The_prefix_is_a_query_parameter_not_query_text()
    {
        using var session = Store.OpenAsyncSession();
        var query = session.Query<MpLocation>().WhereDescendantOf(l => l.Path, "x' or true or '");

        query.ToString().Should().NotContain("or true");
    }

    [Fact]
    public async Task MoveSubtree_rewrites_the_root_and_its_descendants_only()
    {
        await SeedAsync("BRU", "BRU/A", "BRU/A/01", "BRU/A/01/03", "BRU/A10", "BRU/A10/01", "BRU/B");

        var patched = await MaterializedPathQueries.MoveSubtreeAsync<MpLocation>(Store, l => l.Path, "BRU/A", "BRU/B");

        patched.Should().Be(3);
        (await PathsAsync()).Should().Equal("BRU", "BRU/A10", "BRU/A10/01", "BRU/B", "BRU/B/A", "BRU/B/A/01", "BRU/B/A/01/03");
    }

    [Fact]
    public async Task MoveSubtree_to_null_makes_the_moved_node_a_root()
    {
        await SeedAsync("BRU", "BRU/A", "BRU/A/01");

        await MaterializedPathQueries.MoveSubtreeAsync<MpLocation>(Store, l => l.Path, "BRU/A", null);

        (await PathsAsync()).Should().Equal("A", "A/01", "BRU");
    }

    [Fact]
    public async Task MoveSubtree_into_its_own_subtree_is_refused_before_anything_is_sent()
    {
        var act = () => MaterializedPathQueries.MoveSubtreeAsync<MpLocation>(Store, l => l.Path, "BRU/A", "BRU/A/01");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData("Locations where true", "Path")]
    [InlineData("Locations", "Path = 1; this.x")]
    [InlineData("Locations", "Lines[].Path")]
    public void The_move_refuses_an_identifier_it_would_have_to_splice(string collection, string field)
    {
        var act = () => MaterializedPathQueries.BuildMoveRql(collection, field);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_move_passes_every_path_as_a_parameter()
    {
        var rql = MaterializedPathQueries.BuildMoveRql("Locations", "Path");
        var parameters = MaterializedPathQueries.BuildMoveParameters("BRU/A", "BRU/B");

        rql.Should().NotContain("BRU");
        parameters["root"].Should().Be("BRU/A");
        parameters["prefix"].Should().Be("BRU/A/");
        parameters["newRoot"].Should().Be("BRU/B/A");
        parameters["rootLength"].Should().Be(5);
    }
}
