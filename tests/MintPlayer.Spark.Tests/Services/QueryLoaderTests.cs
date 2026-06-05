using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// QueryLoader is the in-memory query catalog: it indexes <see cref="SparkQuery"/> by Id
/// and by alias (auto-generated from Name when not explicitly set). Every query endpoint
/// (<c>/spark/queries/{idOrAlias}</c>, <c>/execute</c>) routes through ResolveQuery, so a
/// regression in alias generation or lookup silently 404s queries that should resolve.
/// </summary>
public class QueryLoaderTests
{
    private static readonly Guid CarsId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid PeopleId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");

    private static SparkQuery Q(Guid id, string name, string? alias = null) => new()
    {
        Id = id,
        Name = name,
        Alias = alias,
        Source = "Database.Items",
    };

    private static QueryLoader CreateLoader(params SparkQuery[] queries)
    {
        var modelLoader = Substitute.For<IModelLoader>();
        modelLoader.GetQueries().Returns(queries);
        return new QueryLoader(modelLoader);
    }

    [Fact]
    public void GetQueries_returns_all_queries_from_the_underlying_model_loader()
    {
        var a = Q(CarsId, "AllCars");
        var b = Q(PeopleId, "AllPeople");
        var loader = CreateLoader(a, b);

        var queries = loader.GetQueries().ToList();

        queries.Should().HaveCount(2);
        queries.Select(q => q.Id).Should().BeEquivalentTo([CarsId, PeopleId]);
    }

    [Fact]
    public void GetQuery_returns_query_by_id()
    {
        var a = Q(CarsId, "AllCars");
        var loader = CreateLoader(a);

        loader.GetQuery(CarsId).Should().BeSameAs(a);
    }

    [Fact]
    public void GetQuery_returns_null_for_unknown_id()
    {
        var loader = CreateLoader(Q(CarsId, "AllCars"));

        loader.GetQuery(Guid.NewGuid()).Should().BeNull();
    }

    [Fact]
    public void GetQueryByName_returns_query_with_matching_name()
    {
        var a = Q(CarsId, "AllCars");
        var loader = CreateLoader(a);

        loader.GetQueryByName("AllCars").Should().BeSameAs(a);
    }

    [Fact]
    public void GetQueryByName_returns_null_when_no_query_has_that_name()
    {
        var loader = CreateLoader(Q(CarsId, "AllCars"));

        loader.GetQueryByName("Nope").Should().BeNull();
    }

    [Fact]
    public void Auto_generated_alias_strips_Get_prefix_and_lowercases()
    {
        // "GetCars" → "cars"
        var loader = CreateLoader(Q(CarsId, "GetCars"));

        loader.GetQueryByAlias("cars").Should().NotBeNull();
        loader.GetQueryByAlias("CARS").Should().NotBeNull("alias lookup is case-insensitive");
    }

    [Fact]
    public void Auto_generated_alias_is_full_name_when_Get_prefix_is_absent()
    {
        var loader = CreateLoader(Q(CarsId, "AllCars"));

        loader.GetQueryByAlias("allcars").Should().NotBeNull();
    }

    [Fact]
    public void Explicit_alias_overrides_auto_generated_one()
    {
        var loader = CreateLoader(Q(CarsId, "GetCars", alias: "vehicles"));

        loader.GetQueryByAlias("vehicles").Should().NotBeNull();
        // Auto-generated "cars" should NOT also be registered.
        loader.GetQueryByAlias("cars").Should().BeNull();
    }

    [Fact]
    public void Duplicate_aliases_keep_the_first_query_first_wins()
    {
        var first = Q(CarsId, "GetCars", alias: "duplicate");
        var second = Q(PeopleId, "GetPeople", alias: "duplicate");
        var loader = CreateLoader(first, second);

        // No throw — the loader logs a warning and keeps the first entry.
        loader.GetQueryByAlias("duplicate").Should().BeSameAs(first);
    }

    [Fact]
    public void ResolveQuery_dispatches_a_Guid_string_to_id_lookup()
    {
        var a = Q(CarsId, "AllCars");
        var loader = CreateLoader(a);

        loader.ResolveQuery(CarsId.ToString()).Should().BeSameAs(a);
    }

    [Fact]
    public void ResolveQuery_dispatches_a_non_Guid_string_to_alias_lookup()
    {
        var a = Q(CarsId, "GetCars");
        var loader = CreateLoader(a);

        loader.ResolveQuery("cars").Should().BeSameAs(a);
    }

    [Fact]
    public void ResolveQuery_returns_null_for_string_that_matches_neither()
    {
        var loader = CreateLoader(Q(CarsId, "GetCars"));

        loader.ResolveQuery("not-a-real-thing").Should().BeNull();
    }

    [Fact]
    public void Underlying_model_loader_is_only_called_once_results_are_cached()
    {
        var modelLoader = Substitute.For<IModelLoader>();
        modelLoader.GetQueries().Returns([Q(CarsId, "AllCars")]);
        var loader = new QueryLoader(modelLoader);

        _ = loader.GetQueries();
        _ = loader.GetQueries();
        _ = loader.GetQuery(CarsId);
        _ = loader.GetQueryByAlias("allcars");

        modelLoader.Received(1).GetQueries();
    }
}
