using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Services.Breadcrumb;
using MintPlayer.Spark.Tests._Infrastructure;
using NSubstitute;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// The row shapes a custom query may and may not return, and what happens to rows once mapped —
/// the three silent failures closed by #327 M1.
/// <para>
/// Each of these used to produce a wrong answer with HTTP 200 and no log line, which is why they
/// are pinned here rather than left to the integration suite: the failure mode is "looks fine".
/// </para>
/// </summary>
public class QueryExecutorRowShapeTests
{
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

    private const string TypeName = "RowShapeEntity";

    private static EntityTypeDefinition Definition(params string[] queryAttributes) => new()
    {
        Id = Guid.Parse("cccc7777-7777-7777-7777-cccccccccccc"),
        Name = TypeName,
        ClrType = typeof(RowShapeEntity).FullName!,
        Attributes = [.. queryAttributes.Select(name => new EntityAttributeDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            DataType = "string",
            ShowedOn = EShowedOn.Query | EShowedOn.PersistentObject,
        })],
    };

    private SparkQuery Bind(string method, EntityTypeDefinition definition, params SortColumn[] sortColumns)
    {
        _modelLoader.GetEntityTypeByName(TypeName).Returns(definition);
        _actionsResolver.ResolveForType(typeof(RowShapeEntity)).Returns(new RowShapeActions());

        return new SparkQuery
        {
            Id = Guid.NewGuid(),
            Name = "RowShapeQuery",
            Source = $"Custom.{method}",
            EntityType = TypeName,
            SortColumns = sortColumns,
        };
    }

    /// <summary>Maps each row to a PO carrying one attribute per named property, so the executor's
    /// post-mapping behaviour (dedup, ordering) is observable without a real mapper.</summary>
    private void MapRowsByReflection(params string[] attributeNames)
    {
        _entityMapper
            .ToPersistentObject(Arg.Any<object>(), Arg.Any<Guid>(), Arg.Any<BreadcrumbResult?>())
            .Returns(call =>
            {
                var row = call.ArgAt<object>(0);
                var type = row.GetType();
                return new PersistentObject
                {
                    Id = type.GetProperty("Id")?.GetValue(row)?.ToString(),
                    Name = TypeName,
                    ObjectTypeId = call.ArgAt<Guid>(1),
                    Attributes = [.. attributeNames.Select(name => new PersistentObjectAttribute
                    {
                        Id = Guid.NewGuid().ToString(),
                        Name = name,
                        DataType = "string",
                        Value = type.GetProperty(name)?.GetValue(row),
                    })],
                };
            });
    }

    [Fact]
    public async Task A_custom_query_returning_PersistentObject_rows_is_refused_and_the_message_names_the_row_type()
    {
        var query = Bind(nameof(RowShapeActions.PersistentObjectRows), Definition());

        var act = () => CreateExecutor().ExecuteQueryAsync(query);

        // The old behaviour: the right number of rows, every cell blank, no error and no log.
        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("PersistentObject");
        ex.Which.Message.Should().Contain("mapped AS an entity",
            "the fix is to return a concrete row type, not to change the method's shape");
    }

    [Theory]
    [InlineData(nameof(RowShapeActions.ObjectRows))]
    [InlineData(nameof(RowShapeActions.DynamicRows))]
    [InlineData(nameof(RowShapeActions.ObjectQueryableRows))]
    public async Task A_custom_query_returning_object_rows_is_refused_however_it_is_declared(string method)
    {
        // The guard used to live only in the interface-scan branch, so a method DECLARED
        // IEnumerable<object> matched the generic-definition branch first and slipped past it.
        var query = Bind(method, Definition());

        var act = () => CreateExecutor().ExecuteQueryAsync(query);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("Object");
        ex.Which.Message.Should().Contain("nothing to reflect");
    }

    [Fact]
    public async Task Rows_that_share_a_null_id_are_all_kept()
    {
        // DistinctBy uses the default comparer, which treats every null key as equal — so an
        // in-memory row type with no readable Id collapsed the whole grid to a single row.
        MapRowsByReflection("Label");
        var query = Bind(nameof(RowShapeActions.IdlessRows), Definition("Label"));

        var result = await CreateExecutor().ExecuteQueryAsync(query);

        result.TotalRecords.Should().Be(3);
        result.Data.Should().HaveCount(3);
    }

    [Fact]
    public async Task Sort_columns_apply_to_a_result_that_was_never_queryable()
    {
        // ApplySorting runs only for an IQueryable, so a plain IEnumerable silently ignored both
        // the query's declared sort and the caller's ?sortColumns= override.
        MapRowsByReflection("Label");
        var query = Bind(
            nameof(RowShapeActions.UnsortedRows),
            Definition("Label"),
            new SortColumn { Property = "Label", Direction = "asc" });

        var result = await CreateExecutor().ExecuteQueryAsync(query);

        result.Data.Select(po => po.Attributes.Single().Value)
            .Should().ContainInOrder("alpha", "beta", "gamma");
    }

    [Fact]
    public async Task Descending_sort_reverses_a_result_that_was_never_queryable()
    {
        MapRowsByReflection("Label");
        var query = Bind(
            nameof(RowShapeActions.UnsortedRows),
            Definition("Label"),
            new SortColumn { Property = "Label", Direction = "desc" });

        var result = await CreateExecutor().ExecuteQueryAsync(query);

        result.Data.Select(po => po.Attributes.Single().Value)
            .Should().ContainInOrder("gamma", "beta", "alpha");
    }

    [Fact]
    public async Task An_in_memory_sort_column_outside_the_query_surface_is_refused_not_applied()
    {
        // Ordering is a comparison oracle over a value the caller may never read, so the in-memory
        // fallback honours the same ShowedOn gate the queryable path does — rows keep index order.
        MapRowsByReflection("Label");
        var definition = Definition("Label");
        definition.Attributes[0].ShowedOn = EShowedOn.PersistentObject;

        var query = Bind(
            nameof(RowShapeActions.UnsortedRows),
            definition,
            new SortColumn { Property = "Label", Direction = "asc" });

        var result = await CreateExecutor().ExecuteQueryAsync(query);

        result.Data.Select(po => po.Attributes.Single().Value)
            .Should().ContainInOrder("gamma", "alpha", "beta");
    }
}

public class RowShapeEntity
{
    public string? Id { get; set; }
    public string? Label { get; set; }
}

public class RowShapeActions
{
    public IEnumerable<PersistentObject> PersistentObjectRows() => [];

    public IEnumerable<object> ObjectRows() => [];

    public IEnumerable<dynamic> DynamicRows() => [];

    public IQueryable<object> ObjectQueryableRows() => Array.Empty<object>().AsQueryable();

    /// <summary>Three rows whose type has no readable id — an aggregate/projection shape.</summary>
    public IEnumerable<RowShapeEntity> IdlessRows() =>
    [
        new() { Label = "one" },
        new() { Label = "two" },
        new() { Label = "three" },
    ];

    /// <summary>A plain sequence, deliberately out of order and deliberately not an IQueryable.</summary>
    public IEnumerable<RowShapeEntity> UnsortedRows() =>
    [
        new() { Id = "rows/3", Label = "gamma" },
        new() { Id = "rows/1", Label = "alpha" },
        new() { Id = "rows/2", Label = "beta" },
    ];
}
