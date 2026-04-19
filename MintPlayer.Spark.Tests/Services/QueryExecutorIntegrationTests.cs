using System.Linq;
using System.Reflection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using NSubstitute;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Integration tests for QueryExecutor that exercise real RavenDB through SparkTestDriver.
/// Database queries reflect on a SparkContext subclass for the IQueryable property; these
/// tests use a fixture context with seeded Person documents.
/// </summary>
public class QueryExecutorIntegrationTests : SparkTestDriver
{
    private static readonly Guid PersonTypeId = Guid.Parse("eeeeeeee-2222-2222-2222-222222222222");

    public class Person
    {
        public string? Id { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
    }

    public class TestSparkContext : SparkContext
    {
        public IRavenQueryable<Person> People => Session.Query<Person>();
    }

    private readonly IModelLoader _modelLoader = Substitute.For<IModelLoader>();
    private readonly ISparkContextResolver _contextResolver = Substitute.For<ISparkContextResolver>();
    private readonly IIndexRegistry _indexRegistry = Substitute.For<IIndexRegistry>();
    private readonly IPermissionService _permissionService = Substitute.For<IPermissionService>();
    private readonly IActionsResolver _actionsResolver = Substitute.For<IActionsResolver>();
    private readonly IReferenceResolver _referenceResolver = Substitute.For<IReferenceResolver>();

    private QueryExecutor CreateExecutor()
    {
        // EntityMapper is concrete + uses real reflection — give it a real instance.
        var entityMapper = new EntityMapper(_modelLoader);

        // Default substitutions
        _modelLoader.GetEntityType(PersonTypeId).Returns(PersonTypeDefinition());
        _modelLoader.GetEntityTypeByClrType(typeof(Person).FullName!).Returns(PersonTypeDefinition());

        _contextResolver.ResolveContext(Arg.Any<IAsyncDocumentSession>())
            .Returns(ci =>
            {
                var ctx = new TestSparkContext();
                typeof(SparkContext)
                    .GetProperty(nameof(SparkContext.Session))!
                    .SetValue(ctx, ci.Arg<IAsyncDocumentSession>());
                return ctx;
            });

        _indexRegistry.GetRegistrationForCollectionType(typeof(Person)).Returns((IndexRegistration?)null);
        _referenceResolver.GetReferenceProperties(typeof(Person), typeof(Person))
            .Returns(new List<(PropertyInfo Property, ReferenceAttribute Attribute)>());
        _referenceResolver.ResolveReferencedDocumentsAsync(
            Arg.Any<IAsyncDocumentSession>(),
            Arg.Any<IList<object>>(),
            Arg.Any<List<(PropertyInfo Property, ReferenceAttribute Attribute)>>())
            .Returns(new Dictionary<string, object>());

        return new QueryExecutor(
            Store, entityMapper, _modelLoader, _contextResolver,
            _indexRegistry, _permissionService, _actionsResolver, _referenceResolver);
    }

    private static EntityTypeDefinition PersonTypeDefinition() => new()
    {
        Id = PersonTypeId,
        Name = "Person",
        ClrType = typeof(Person).FullName!,
        DisplayAttribute = "LastName",
        Attributes =
        [
            new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "FirstName", DataType = "string" },
            new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "LastName", DataType = "string" },
        ]
    };

    private async Task SeedPeopleAsync(params (string id, string first, string last)[] people)
    {
        using var session = Store.OpenAsyncSession();
        foreach (var (id, first, last) in people)
        {
            await session.StoreAsync(new Person { FirstName = first, LastName = last }, id);
        }
        await session.SaveChangesAsync();
        WaitForIndexing(Store);
    }

    private static SparkQuery DatabasePeopleQuery() => new()
    {
        Id = Guid.NewGuid(),
        Name = "AllPeople",
        Source = "Database.People",
    };

    [Fact]
    public async Task Database_query_returns_all_seeded_documents()
    {
        await SeedPeopleAsync(
            ("people/1", "Alice", "Smith"),
            ("people/2", "Bob", "Jones"),
            ("people/3", "Carol", "Davis"));

        var executor = CreateExecutor();

        var result = await executor.ExecuteQueryAsync(DatabasePeopleQuery());

        result.TotalRecords.Should().Be(3);
        result.Data.Select(p => p.Id).Should().BeEquivalentTo(["people/1", "people/2", "people/3"]);
    }

    [Fact]
    public async Task Database_query_calls_permission_service_with_Query_action()
    {
        await SeedPeopleAsync(("people/1", "Alice", "Smith"));
        var executor = CreateExecutor();

        await executor.ExecuteQueryAsync(DatabasePeopleQuery());

        await _permissionService.Received(1).EnsureAuthorizedAsync("Query", "Person", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Database_query_propagates_permission_denial()
    {
        await SeedPeopleAsync(("people/1", "Alice", "Smith"));
        _permissionService
            .EnsureAuthorizedAsync("Query", "Person", Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new SparkAccessDeniedException("denied")));
        var executor = CreateExecutor();

        var act = () => executor.ExecuteQueryAsync(DatabasePeopleQuery());

        await act.Should().ThrowAsync<SparkAccessDeniedException>();
    }

    [Fact]
    public async Task Search_filters_results_by_attribute_value_substring()
    {
        await SeedPeopleAsync(
            ("people/1", "Alice", "Smith"),
            ("people/2", "Bob", "Jones"),
            ("people/3", "Carol", "Davis"));
        var executor = CreateExecutor();

        var result = await executor.ExecuteQueryAsync(DatabasePeopleQuery(), search: "alice");

        result.TotalRecords.Should().Be(1);
        result.Data.Should().ContainSingle().Which.Id.Should().Be("people/1");
    }

    [Fact]
    public async Task Search_is_case_insensitive()
    {
        await SeedPeopleAsync(
            ("people/1", "Alice", "Smith"),
            ("people/2", "Bob", "Jones"));
        var executor = CreateExecutor();

        var result = await executor.ExecuteQueryAsync(DatabasePeopleQuery(), search: "SMITH");

        result.TotalRecords.Should().Be(1);
    }

    [Fact]
    public async Task Pagination_skips_and_takes_correctly()
    {
        await SeedPeopleAsync(
            Enumerable.Range(1, 10)
                .Select(i => ($"people/{i}", $"First{i}", $"Last{i}"))
                .ToArray());
        var executor = CreateExecutor();

        var result = await executor.ExecuteQueryAsync(DatabasePeopleQuery(), skip: 3, take: 4);

        result.TotalRecords.Should().Be(10);   // unfiltered total
        result.Data.Count().Should().Be(4);    // only the page
        result.Skip.Should().Be(3);
        result.Take.Should().Be(4);
    }

    [Fact]
    public async Task Empty_database_returns_empty_result()
    {
        var executor = CreateExecutor();

        var result = await executor.ExecuteQueryAsync(DatabasePeopleQuery());

        result.TotalRecords.Should().Be(0);
        result.Data.Should().BeEmpty();
    }
}
