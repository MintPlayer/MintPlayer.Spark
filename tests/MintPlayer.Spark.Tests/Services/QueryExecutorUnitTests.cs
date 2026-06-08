using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Services.Breadcrumb;
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

    private readonly IAsyncDocumentSession _session = Substitute.For<IAsyncDocumentSession>();
    private readonly IEntityMapper _entityMapper = Substitute.For<IEntityMapper>();
    private readonly IModelLoader _modelLoader = Substitute.For<IModelLoader>();
    private readonly ISparkContextResolver _contextResolver = Substitute.For<ISparkContextResolver>();
    private readonly IIndexRegistry _indexRegistry = Substitute.For<IIndexRegistry>();
    private readonly IPermissionService _permissionService = Substitute.For<IPermissionService>();
    private readonly IActionsResolver _actionsResolver = Substitute.For<IActionsResolver>();
    private readonly IReferenceResolver _referenceResolver = Substitute.For<IReferenceResolver>();
    private readonly IBreadcrumbResolver _breadcrumbResolver = Substitute.For<IBreadcrumbResolver>();

    private QueryExecutor CreateExecutor() => new(
        _session, _entityMapper, _modelLoader, _contextResolver,
        _indexRegistry, _permissionService, _actionsResolver, _referenceResolver, _breadcrumbResolver);

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

    [Fact]
    public async Task CustomQuery_method_cache_stays_consistent_under_parallel_load()
    {
        // The custom-query method-info cache is a static ConcurrentDictionary keyed by
        // "{ActionsTypeName};{MethodName}". GetOrAdd is thread-safe but its factory may
        // run more than once under contention — the contract we depend on is that all
        // observers see the same cached value once the dust settles, with no exceptions
        // and no corruption. This test fires N parallel resolutions of the same query
        // through the executor and asserts every one returns identical empty data.
        var entityDef = new EntityTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = "QECacheConcurrencyEntity",
            ClrType = typeof(QECacheTestEntity).FullName!,
        };
        _modelLoader.GetEntityTypeByName("QECacheConcurrencyEntity").Returns(entityDef);
        _actionsResolver.ResolveForType(typeof(QECacheTestEntity)).Returns(new QECacheTestActions());

        var query = new SparkQuery
        {
            Id = Guid.NewGuid(),
            Name = "QECacheConcurrencyQuery",
            Source = "Custom.EmptyPeople",
            EntityType = "QECacheConcurrencyEntity",
        };

        var executor = CreateExecutor();

        // 16 parallel callers — well above any practical concurrency for this code path.
        var results = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => executor.ExecuteQueryAsync(query))));

        results.Should().AllSatisfy(r =>
        {
            r.Data.Should().BeEmpty();
            r.TotalRecords.Should().Be(0);
        });
    }

    public class QECacheTestEntity { public string? Id { get; set; } }

    public class QECacheTestActions
    {
        public IQueryable<QECacheTestEntity> EmptyPeople() => Array.Empty<QECacheTestEntity>().AsQueryable();
    }
}
