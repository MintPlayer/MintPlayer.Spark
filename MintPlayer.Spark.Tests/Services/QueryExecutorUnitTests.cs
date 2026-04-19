using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;
using NSubstitute;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Pure-mock unit tests for QueryExecutor — covers source parsing, empty-path
/// short-circuits, and search/pagination logic without spinning up RavenDB.
/// Integration tests for happy-path execution live in QueryExecutorIntegrationTests.
/// </summary>
public class QueryExecutorUnitTests
{
    private static readonly Guid PersonTypeId = Guid.Parse("dddddddd-1111-1111-1111-111111111111");

    private readonly IDocumentStore _store = Substitute.For<IDocumentStore>();
    private readonly IEntityMapper _entityMapper = Substitute.For<IEntityMapper>();
    private readonly IModelLoader _modelLoader = Substitute.For<IModelLoader>();
    private readonly ISparkContextResolver _contextResolver = Substitute.For<ISparkContextResolver>();
    private readonly IIndexRegistry _indexRegistry = Substitute.For<IIndexRegistry>();
    private readonly IPermissionService _permissionService = Substitute.For<IPermissionService>();
    private readonly IActionsResolver _actionsResolver = Substitute.For<IActionsResolver>();
    private readonly IReferenceResolver _referenceResolver = Substitute.For<IReferenceResolver>();

    public QueryExecutorUnitTests()
    {
        _store.OpenAsyncSession().Returns(Substitute.For<IAsyncDocumentSession>());
    }

    private QueryExecutor CreateExecutor() => new(
        _store, _entityMapper, _modelLoader, _contextResolver,
        _indexRegistry, _permissionService, _actionsResolver, _referenceResolver);

    private static SparkQuery Q(string source) => new()
    {
        Id = Guid.NewGuid(),
        Name = "TestQuery",
        Source = source,
    };

    [Fact]
    public async Task Throws_when_source_has_no_known_prefix()
    {
        var executor = CreateExecutor();

        var act = () => executor.ExecuteQueryAsync(Q("Invalid.Stuff"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(e => e.Message.Contains("invalid Source") && e.Message.Contains("TestQuery"));
    }

    [Fact]
    public async Task Throws_when_source_is_empty_string()
    {
        var executor = CreateExecutor();

        var act = () => executor.ExecuteQueryAsync(Q(string.Empty));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Database_query_returns_empty_when_SparkContext_unresolved()
    {
        _contextResolver.ResolveContext(Arg.Any<IAsyncDocumentSession>()).Returns((SparkContext?)null);
        var executor = CreateExecutor();

        var result = await executor.ExecuteQueryAsync(Q("Database.People"));

        result.Data.Should().BeEmpty();
        result.TotalRecords.Should().Be(0);
    }

    private sealed class EmptyContext : SparkContext { }

    [Fact]
    public async Task Database_query_returns_empty_when_property_does_not_exist_on_context()
    {
        _contextResolver.ResolveContext(Arg.Any<IAsyncDocumentSession>()).Returns(new EmptyContext());
        var executor = CreateExecutor();

        var result = await executor.ExecuteQueryAsync(Q("Database.NoSuchProperty"));

        result.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task Custom_query_returns_empty_when_EntityType_is_not_set()
    {
        // EntityType is null on SparkQuery → ResolveEntityTypeDefinition returns null → empty
        var executor = CreateExecutor();

        var result = await executor.ExecuteQueryAsync(Q("Custom.SomeMethod"));

        result.Data.Should().BeEmpty();
        result.TotalRecords.Should().Be(0);
    }

    [Fact]
    public async Task Custom_prefix_match_is_case_insensitive()
    {
        var executor = CreateExecutor();

        // No EntityType → empty data, but the prefix-matching branch was taken without throwing
        var result = await executor.ExecuteQueryAsync(Q("custom.Anything"));

        result.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task Database_prefix_match_is_case_insensitive()
    {
        _contextResolver.ResolveContext(Arg.Any<IAsyncDocumentSession>()).Returns((SparkContext?)null);
        var executor = CreateExecutor();

        var result = await executor.ExecuteQueryAsync(Q("DATABASE.People"));

        result.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task Pagination_skip_and_take_default_to_full_result_set()
    {
        var executor = CreateExecutor();

        var result = await executor.ExecuteQueryAsync(Q("Database.People"));

        result.Skip.Should().Be(0);
        result.Take.Should().Be(50);
    }

    [Fact]
    public async Task Pagination_skip_and_take_are_propagated_to_result_envelope()
    {
        var executor = CreateExecutor();

        var result = await executor.ExecuteQueryAsync(Q("Database.People"), skip: 25, take: 10);

        result.Skip.Should().Be(25);
        result.Take.Should().Be(10);
    }
}
