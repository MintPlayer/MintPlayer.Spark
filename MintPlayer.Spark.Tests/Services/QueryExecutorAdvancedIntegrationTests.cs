using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Integration tests targeting the reflective dispatch tail of <see cref="QueryExecutor"/>:
/// custom-query method invocation (cached <see cref="System.Reflection.MethodInfo"/>),
/// sorting via <c>typeof(Queryable).GetMethods()</c> + <c>MakeGenericMethod</c>,
/// reference-include resolution via cached generic <c>LoadAsync&lt;T&gt;</c>, and the
/// custom-query async + sync queryable shapes that <c>ResolveCustomQueryMethod</c> caches.
///
/// These paths can only be exercised with a real document store — the cached MethodInfos
/// only fire when the actions pipeline runs end-to-end.
/// </summary>
public class QueryExecutorAdvancedIntegrationTests : SparkTestDriver
{
    private static readonly Guid CompanyTypeId = Guid.Parse("aaaa1111-aaaa-aaaa-aaaa-aaaa11111111");
    private static readonly Guid EmployeeTypeId = Guid.Parse("bbbb2222-bbbb-bbbb-bbbb-bbbb22222222");

    public class Company
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public class Employee
    {
        public string? Id { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;

        [Reference(typeof(Company))]
        public string? Company { get; set; }
    }

    public class TestContext : SparkContext
    {
        public IRavenQueryable<Company> Companies => Session.Query<Company>();
        public IRavenQueryable<Employee> Employees => Session.Query<Employee>();
    }

    private static EntityTypeFile CompanyModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = CompanyTypeId,
            Name = "Company",
            ClrType = typeof(Company).FullName!,
            DisplayAttribute = "Name",
            Attributes = [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Name", DataType = "string" },
            ],
        },
    };

    private static EntityTypeFile EmployeeModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = EmployeeTypeId,
            Name = "Employee",
            ClrType = typeof(Employee).FullName!,
            DisplayAttribute = "LastName",
            Attributes = [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "FirstName", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "LastName", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Company", DataType = "Reference" },
            ],
        },
    };

    private SparkEndpointFactory<TestContext> _factory = null!;
    private IQueryExecutor _executor = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _factory = new SparkEndpointFactory<TestContext>(Store, [CompanyModel(), EmployeeModel()]);
        _executor = _factory.GetService<IQueryExecutor>();
    }

    public override async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private async Task<(string companyId, string[] employeeIds)> SeedAsync()
    {
        using var session = Store.OpenAsyncSession();
        var company = new Company { Name = "Acme" };
        await session.StoreAsync(company);
        var employees = new[]
        {
            new Employee { FirstName = "Ada", LastName = "Lovelace", Company = company.Id },
            new Employee { FirstName = "Grace", LastName = "Hopper", Company = company.Id },
            new Employee { FirstName = "Linus", LastName = "Torvalds", Company = company.Id },
        };
        foreach (var e in employees) await session.StoreAsync(e);
        await session.SaveChangesAsync();
        WaitForIndexing(Store);
        return (company.Id!, employees.Select(e => e.Id!).ToArray());
    }

    // --- Database query with reference Include() ---------------------------

    [Fact]
    public async Task Database_query_resolves_reference_breadcrumbs_via_ApplyIncludes()
    {
        // Exercises the entire reference-resolution chain:
        //   - ReferenceResolver.GetReferenceProperties (cached PropertyInfo + ReferenceAttribute)
        //   - ReferenceResolver.ApplyIncludes (cached MethodInfo for queryable.Include(string))
        //   - ReferenceResolver.ResolveReferencedDocumentsAsync (cached LoadAsync<T>)
        //   - EntityMapper.PopulateAttributeValues (sets Breadcrumb from includedDocuments)
        var (companyId, _) = await SeedAsync();

        var query = new SparkQuery
        {
            Id = Guid.NewGuid(),
            Name = "Employees",
            Source = "Database.Employees",
        };

        var result = await _executor.ExecuteQueryAsync(query);

        result.TotalRecords.Should().Be(3);
        var first = result.Data.First();
        var companyAttr = first.Attributes.Single(a => a.Name == "Company");
        companyAttr.Value.Should().Be(companyId);
        companyAttr.Breadcrumb.Should().Be("Acme",
            "the breadcrumb comes from the cached LoadAsync<Company> dispatch");
    }

    // --- Database query with sorting --------------------------------------

    [Fact]
    public async Task Database_query_sorts_results_via_reflective_OrderBy_call()
    {
        // ApplySorting reflects on typeof(Queryable).GetMethods() and calls MakeGenericMethod
        // on the matching OrderBy / OrderByDescending overload — these closed MethodInfos
        // are cached per (entityType, propertyType, methodName).
        await SeedAsync();

        var query = new SparkQuery
        {
            Id = Guid.NewGuid(),
            Name = "EmployeesSorted",
            Source = "Database.Employees",
            SortColumns = [
                new SortColumn { Property = "LastName", Direction = "asc" },
            ],
        };

        var result = await _executor.ExecuteQueryAsync(query);

        var lastNames = result.Data
            .Select(po => po.Attributes.Single(a => a.Name == "LastName").Value?.ToString())
            .ToList();

        lastNames.Should().Equal("Hopper", "Lovelace", "Torvalds");
    }

    [Fact]
    public async Task Database_query_sorts_results_descending()
    {
        await SeedAsync();

        var query = new SparkQuery
        {
            Id = Guid.NewGuid(),
            Name = "EmployeesSortedDesc",
            Source = "Database.Employees",
            SortColumns = [
                new SortColumn { Property = "LastName", Direction = "desc" },
            ],
        };

        var result = await _executor.ExecuteQueryAsync(query);

        var lastNames = result.Data
            .Select(po => po.Attributes.Single(a => a.Name == "LastName").Value?.ToString())
            .ToList();

        lastNames.Should().Equal("Torvalds", "Lovelace", "Hopper");
    }

    [Fact]
    public async Task Database_query_supports_multi_column_sort()
    {
        // Multi-column sort drives the i==0/ThenBy branch in ApplySorting.
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new Employee { FirstName = "B", LastName = "Z" });
            await session.StoreAsync(new Employee { FirstName = "A", LastName = "Z" });
            await session.StoreAsync(new Employee { FirstName = "B", LastName = "A" });
            await session.SaveChangesAsync();
        }
        WaitForIndexing(Store);

        var query = new SparkQuery
        {
            Id = Guid.NewGuid(),
            Name = "EmployeesMultiSort",
            Source = "Database.Employees",
            SortColumns = [
                new SortColumn { Property = "LastName", Direction = "asc" },
                new SortColumn { Property = "FirstName", Direction = "asc" },
            ],
        };

        var result = await _executor.ExecuteQueryAsync(query);

        var ordered = result.Data
            .Select(po => (po.Attributes.Single(a => a.Name == "LastName").Value?.ToString(),
                           po.Attributes.Single(a => a.Name == "FirstName").Value?.ToString()))
            .ToList();

        ordered.Should().Equal(("A", "B"), ("Z", "A"), ("Z", "B"));
    }

    // --- Custom query path -------------------------------------------------

    /// <summary>
    /// Custom-query Actions class that returns a real IRavenQueryable<T> via the session.
    /// The session is supplied through DI by SparkEndpointFactory's scoped registration.
    /// </summary>
    public class EmployeeActions : DefaultPersistentObjectActions<Employee>
    {
        private readonly IAsyncDocumentSession _session;
        public EmployeeActions(IEntityMapper entityMapper, IAsyncDocumentSession session) : base(entityMapper)
        {
            _session = session;
        }

        public IRavenQueryable<Employee> AllEmployees(CustomQueryArgs _) => _session.Query<Employee>();
        public IRavenQueryable<Employee> NoArgs() => _session.Query<Employee>();
        // Async returns Task<IEnumerable<T>> (already-materialized) — that's the only async
        // shape the executor supports cleanly; Task<IRavenQueryable<T>> would synchronously
        // enumerate the Raven query after awaiting and Raven rejects sync ops on async sessions.
        public async Task<IEnumerable<Employee>> AllEmployeesAsync(CustomQueryArgs _)
        {
            return await _session.Query<Employee>().ToListAsync();
        }
        public IQueryable<Employee> InMemoryEmployees() => new[]
        {
            new Employee { Id = "memory/1", FirstName = "InMemory", LastName = "Entity" },
        }.AsQueryable();
    }

    // EmployeeActions is discovered through the framework's normal Tier-1 path:
    // ActionsResolver.FindActionsType walks the loaded assemblies for a public class
    // named "{EntityName}Actions" and constructs it via ActivatorUtilities (which pulls
    // IEntityMapper + IAsyncDocumentSession from the DI scope). No explicit registration
    // is needed; we deliberately rely on the same convention production apps use.
    private IQueryExecutor CustomExecutor() => _factory.GetService<IQueryExecutor>();

    [Fact]
    public async Task Custom_query_with_sync_IRavenQueryable_executes_via_cached_MethodInfo()
    {
        await SeedAsync();
        var executor = CustomExecutor();

        var query = new SparkQuery
        {
            Id = Guid.NewGuid(),
            Name = "CustomEmployeesAll",
            Source = "Custom.AllEmployees",
            EntityType = "Employee",
        };

        var result = await executor.ExecuteQueryAsync(query);

        result.TotalRecords.Should().Be(3);
    }

    [Fact]
    public async Task Custom_query_with_zero_args_method_executes_via_cached_MethodInfo()
    {
        await SeedAsync();
        var executor = CustomExecutor();

        var query = new SparkQuery
        {
            Id = Guid.NewGuid(),
            Name = "CustomEmployeesNoArgs",
            Source = "Custom.NoArgs",
            EntityType = "Employee",
        };

        var result = await executor.ExecuteQueryAsync(query);

        result.TotalRecords.Should().Be(3);
    }

    [Fact]
    public async Task Custom_query_with_async_method_unwraps_Task_result_via_GetCompletedTaskResult()
    {
        await SeedAsync();
        var executor = CustomExecutor();

        var query = new SparkQuery
        {
            Id = Guid.NewGuid(),
            Name = "CustomEmployeesAsync",
            Source = "Custom.AllEmployeesAsync",
            EntityType = "Employee",
        };

        var result = await executor.ExecuteQueryAsync(query);

        result.TotalRecords.Should().Be(3);
    }

    [Fact]
    public async Task Custom_query_with_in_memory_IQueryable_uses_MaterializeQueryable()
    {
        var executor = CustomExecutor();

        var query = new SparkQuery
        {
            Id = Guid.NewGuid(),
            Name = "CustomInMemory",
            Source = "Custom.InMemoryEmployees",
            EntityType = "Employee",
        };

        var result = await executor.ExecuteQueryAsync(query);

        result.TotalRecords.Should().Be(1);
        result.Data.Single().Id.Should().Be("memory/1");
    }

    [Fact]
    public async Task Custom_query_throws_for_method_with_unsupported_signature()
    {
        // ResolveCustomQueryMethod returns null for invalid signatures; the executor
        // converts that to a clear InvalidOperationException.
        var executor = CustomExecutor();

        var query = new SparkQuery
        {
            Id = Guid.NewGuid(),
            Name = "Bogus",
            Source = "Custom.NoSuchMethod",
            EntityType = "Employee",
        };

        var act = () => executor.ExecuteQueryAsync(query);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(e => e.Message.Contains("not found"));
    }
}
