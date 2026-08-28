using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
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
            Breadcrumb = "{Name}",
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
            Breadcrumb = "{LastName}",
            Attributes = [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "FirstName", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "LastName", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Company", DataType = "Reference", ReferenceType = typeof(Company).FullName },
            ],
        },
    };

    private SparkEndpointFactory<TestContext> _factory = null!;
    private IQueryExecutor _executor = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _factory = new SparkEndpointFactory<TestContext>(Store, [CompanyModel(), EmployeeModel()],
            configureIndexCatalog: catalog =>
            {
                catalog.RegisterIndex(typeof(Employees_ByLastName));
                catalog.RegisterProjection(typeof(VEmployee), typeof(Employees_ByLastName));
            });
        _executor = _factory.GetService<IQueryExecutor>();
    }

    public override async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private async Task<(string companyId, string[] employeeIds)> SeedAsync()
    {
        var company = new Company { Name = "Acme" };
        Employee[] employees = null!;

        await base.SeedAsync(async session =>
        {
            // Stored first so the generated company id is available to reference below.
            await session.StoreAsync(company);
            employees =
            [
                new Employee { FirstName = "Ada", LastName = "Lovelace", Company = company.Id },
                new Employee { FirstName = "Grace", LastName = "Hopper", Company = company.Id },
                new Employee { FirstName = "Linus", LastName = "Torvalds", Company = company.Id },
            ];
            foreach (var e in employees) await session.StoreAsync(e);
        });

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

        result.TotalItems.Should().Be(3);
        var first = result.Items.First();
        var companyAttr = first.Values.Single(a => a.Key == "Company");
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

        var lastNames = result.Items
            .Select(po => po.Values.Single(a => a.Key == "LastName").Value?.ToString())
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

        var lastNames = result.Items
            .Select(po => po.Values.Single(a => a.Key == "LastName").Value?.ToString())
            .ToList();

        lastNames.Should().Equal("Torvalds", "Lovelace", "Hopper");
    }

    [Fact]
    public async Task Database_query_supports_multi_column_sort()
    {
        // Multi-column sort drives the i==0/ThenBy branch in ApplySorting.
        await base.SeedAsync(async session =>
        {
            await session.StoreAsync(new Employee { FirstName = "B", LastName = "Z" });
            await session.StoreAsync(new Employee { FirstName = "A", LastName = "Z" });
            await session.StoreAsync(new Employee { FirstName = "B", LastName = "A" });
        });

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

        var ordered = result.Items
            .Select(po => (po.Values.Single(a => a.Key == "LastName").Value?.ToString(),
                           po.Values.Single(a => a.Key == "FirstName").Value?.ToString()))
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
        // Already-materialized async shape. Task<IRavenQueryable<T>> is equally supported since
        // #294 — capabilities come from the runtime result, so an awaited Raven queryable takes
        // the async path rather than a blocking ToList(). See AsyncCustomQueryTests.
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

        result.TotalItems.Should().Be(3);
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

        result.TotalItems.Should().Be(3);
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

        result.TotalItems.Should().Be(3);
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

        result.TotalItems.Should().Be(1);
        result.Items.Single().Id.Should().Be("memory/1");
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

    // --- Index + projection path ------------------------------------------

    /// <summary>
    /// Map index over Employee that projects FirstName + LastName as stored fields.
    /// Drives QueryExecutor.ApplyIndexWithType + ApplyProjection through the registered
    /// projection-type path.
    /// </summary>
    public class Employees_ByLastName : AbstractIndexCreationTask<Employee>
    {
        public Employees_ByLastName()
        {
            Map = employees => from e in employees
                               select new
                               {
                                   e.FirstName,
                                   e.LastName,
                               };
            StoreAllFields(FieldStorage.Yes);
        }
    }

    /// <summary>Projection type for the index.</summary>
    public class VEmployee
    {
        public string? Id { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
    }

    [Fact]
    public async Task Database_query_through_declared_index_uses_ApplyIndexWithType_and_ApplyProjection()
    {
        // Exercises the cached MethodInfos for:
        //   - IAsyncDocumentSession.Query<TResult, TIndexCreator>() (zero-arg overload)
        //   - LinqExtensions.ProjectInto<T>(IQueryable)
        //   - LinqExtensions.ToListAsync<T>(IQueryable, CancellationToken)
        // The binding is declared on the query (#279) — nothing resolves by collection type.
        await SeedAsync();

        await new Employees_ByLastName().ExecuteAsync(Store);
        await Store.WaitForIndexingAsync();

        var query = new SparkQuery
        {
            Id = Guid.NewGuid(),
            Name = "EmployeesByIndex",
            Source = "Database.Employees",
            IndexName = "Employees_ByLastName",
        };

        var result = await _executor.ExecuteQueryAsync(query);

        result.TotalItems.Should().Be(3);
    }

    [Fact]
    public async Task Database_query_through_index_supports_sorting_on_indexed_field()
    {
        await SeedAsync();

        await new Employees_ByLastName().ExecuteAsync(Store);
        await Store.WaitForIndexingAsync();

        var query = new SparkQuery
        {
            Id = Guid.NewGuid(),
            Name = "EmployeesByIndexSorted",
            Source = "Database.Employees",
            IndexName = "Employees_ByLastName",
            SortColumns = [
                new SortColumn { Property = "LastName", Direction = "asc" },
            ],
        };

        var result = await _executor.ExecuteQueryAsync(query);

        var lastNames = result.Items
            .Select(po => po.Values.Single(a => a.Key == "LastName").Value?.ToString())
            .ToList();

        lastNames.Should().Equal("Hopper", "Lovelace", "Torvalds");
    }

    [Fact]
    public async Task Database_query_with_unknown_IndexName_throws_instead_of_falling_back()
    {
        // A declared name is authoritative (#279): naming an index the catalog doesn't know is an
        // error, never a silent raw-collection query with null computed fields.
        await SeedAsync();

        var query = new SparkQuery
        {
            Id = Guid.NewGuid(),
            Name = "EmployeesByUnknownIndex",
            Source = "Database.Employees",
            IndexName = "Employees_DoesNotExist",
        };

        var act = () => _executor.ExecuteQueryAsync(query);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(e => e.Message.Contains("Employees_DoesNotExist"));
    }

    [Fact]
    public async Task Database_query_without_indexName_falls_back_to_the_entity_files_declared_binding()
    {
        // R279.4: query.indexName → entity file's queryType/indexName → raw collection. Here the
        // query carries no name, the model does — the model-declared default routes through the
        // index and its projection.
        await SeedAsync();

        await new Employees_ByLastName().ExecuteAsync(Store);
        await Store.WaitForIndexingAsync();

        var employeeModel = EmployeeModel();
        employeeModel.PersistentObject.QueryType = typeof(VEmployee).FullName;
        employeeModel.PersistentObject.IndexName = "Employees_ByLastName";

        await using var factory = new SparkEndpointFactory<TestContext>(Store, [CompanyModel(), employeeModel],
            configureIndexCatalog: catalog =>
            {
                catalog.RegisterIndex(typeof(Employees_ByLastName));
                catalog.RegisterProjection(typeof(VEmployee), typeof(Employees_ByLastName));
            });
        var executor = factory.GetService<IQueryExecutor>();

        var query = new SparkQuery
        {
            Id = Guid.NewGuid(),
            Name = "EmployeesByModelBinding",
            Source = "Database.Employees",
        };

        var result = await executor.ExecuteQueryAsync(query);

        result.TotalItems.Should().Be(3);
    }
}
