using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Services.Breadcrumb;
using NSubstitute;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using MintPlayer.Spark.Tests._Infrastructure;

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
    private readonly IIndexCatalog _indexCatalog = Substitute.For<IIndexCatalog>();
    private readonly IPermissionService _permissionService = Substitute.For<IPermissionService>();
    private readonly IActionsResolver _actionsResolver = Substitute.For<IActionsResolver>();
    private readonly IReferenceResolver _referenceResolver = Substitute.For<IReferenceResolver>();
    private readonly IBreadcrumbResolver _breadcrumbResolver = Substitute.For<IBreadcrumbResolver>();

    private QueryExecutor CreateExecutor() => new(
        _session, _entityMapper, _modelLoader, _contextResolver,
        _indexCatalog, _permissionService, _actionsResolver, _referenceResolver, _breadcrumbResolver,
        new PermissiveRowSecurity());

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
    public async Task Database_query_without_a_SparkContext_says_so()
    {
        // #327 M6. This used to return an empty grid, which is the same thing a correctly
        // configured query over an empty collection returns — so an application that forgot to
        // register a context looked like one with no data.
        _contextResolver.ResolveContext(Arg.Any<IAsyncDocumentSession>()).Returns((SparkContext?)null);
        var executor = CreateExecutor();

        var act = () => executor.ExecuteQueryAsync(Q("Database.People"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(e => e.Message.Contains("registers no SparkContext"));
    }

    private sealed class EmptyContext : SparkContext { }

    [Fact]
    public async Task Database_query_naming_a_property_the_context_does_not_have_says_so()
    {
        _contextResolver.ResolveContext(Arg.Any<IAsyncDocumentSession>()).Returns(new EmptyContext());
        var executor = CreateExecutor();

        var act = () => executor.ExecuteQueryAsync(Q("Database.NoSuchProperty"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(e => e.Message.Contains("NoSuchProperty") && e.Message.Contains("EmptyContext"));
    }

    [Fact]
    public async Task Custom_query_without_an_entityType_says_what_to_set()
    {
        // A custom query's columns come from its declared type, so there is nothing to infer from
        // the method's return type. Naming the missing field beats an empty grid.
        var executor = CreateExecutor();

        var act = () => executor.ExecuteQueryAsync(Q("Custom.SomeMethod"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(e => e.Message.Contains("names no entityType"));
    }

    [Fact]
    public async Task Custom_prefix_match_is_case_insensitive()
    {
        var executor = CreateExecutor();

        // The subject is the prefix match, not what follows it: 'custom.' routes to the custom
        // path, which then fails on the missing entityType rather than on an unknown source.
        var act = () => executor.ExecuteQueryAsync(Q("custom.Anything"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(e => e.Message.Contains("names no entityType"));
    }

    [Fact]
    public async Task Database_prefix_match_is_case_insensitive()
    {
        _contextResolver.ResolveContext(Arg.Any<IAsyncDocumentSession>()).Returns((SparkContext?)null);
        var executor = CreateExecutor();

        var act = () => executor.ExecuteQueryAsync(Q("DATABASE.People"));

        // Reaching the missing-context message at all proves 'DATABASE.' matched the database
        // branch; an unmatched prefix throws about the source instead.
        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(e => e.Message.Contains("registers no SparkContext"));
    }

    /// <summary>
    /// A query the executor can actually run to completion — needed now that a misconfigured one
    /// throws instead of quietly answering with an empty envelope (#327 M6). The rows are empty,
    /// which is all the envelope tests care about.
    /// </summary>
    private SparkQuery WorkingCustomQuery()
    {
        _modelLoader.GetEntityTypeByName("QEEnvelopeEntity").Returns(new EntityTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = "QEEnvelopeEntity",
            ClrType = typeof(QECacheTestEntity).FullName!,
        });
        _actionsResolver.ResolveForType(typeof(QECacheTestEntity)).Returns(new QECacheTestActions());

        return new SparkQuery
        {
            Id = Guid.NewGuid(),
            Name = "QEEnvelopeQuery",
            Source = "Custom.EmptyPeople",
            EntityType = "QEEnvelopeEntity",
        };
    }

    [Fact]
    public async Task Pagination_skip_and_take_default_to_full_result_set()
    {
        var executor = CreateExecutor();

        var result = await executor.ExecuteQueryAsync(WorkingCustomQuery());

        result.Skip.Should().Be(0);
        result.Take.Should().Be(50);
    }

    [Fact]
    public async Task Pagination_skip_and_take_are_propagated_to_result_envelope()
    {
        var executor = CreateExecutor();

        var result = await executor.ExecuteQueryAsync(WorkingCustomQuery(), skip: 25, take: 10);

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
            r.Items.Should().BeEmpty();
            r.TotalItems.Should().Be(0);
        });
    }

    public class QECacheTestEntity { public string? Id { get; set; } }

    public class QECacheTestActions
    {
        public IQueryable<QECacheTestEntity> EmptyPeople() => Array.Empty<QECacheTestEntity>().AsQueryable();
    }
}
