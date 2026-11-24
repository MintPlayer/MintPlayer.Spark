// #536 spike S2b: Spark's real QueryExecutor over the EF prototype (EfSpark.cs), on SQL Server and PostgreSQL.
// Gate question: do the row filter, restrict-to-ids and search run as SQL, unchanged, over D1/D25/D26 tables?
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;

// The SQL log is process-wide: one test class at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace S2b;

public class Company { public string? Id { get; set; } public string Name { get; set; } = ""; }
public class Skill { public string? Id { get; set; } public string Name { get; set; } = ""; }
public class Address { public string? Id { get; set; } public string Street { get; set; } = ""; public string City { get; set; } = ""; }

public class Vehicle { public string? Id { get; set; } public string Plate { get; set; } = ""; }
public class Car : Vehicle { public int Seats { get; set; } }
public class Truck : Vehicle { public int Axles { get; set; } }

public class Employee
{
    public string? Id { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string Owner { get; set; } = "";

    // O13: a stored computed member, and the backing field the generator would emit for it.
    public string FullName => $"{FirstName} {LastName}";
#pragma warning disable CS0169, IDE0051
    private string? __stored_FullName;
#pragma warning restore CS0169, IDE0051

    [Reference(typeof(Company))] public string? Company { get; set; }
    public TranslatedString? Title { get; set; }
    public List<Address> Addresses { get; set; } = [];
    [Reference(typeof(Skill))] public List<string> Skills { get; set; } = [];
}

/// <summary>The row filter: Read and Query see only alice's employees.</summary>
public class EmployeeActions(IEntityMapper entityMapper) : DefaultPersistentObjectActions<Employee>(entityMapper)
{
    public override Task<Expression<Func<Employee, bool>>?> GetRowFilterAsync(string action)
        => Task.FromResult<Expression<Func<Employee, bool>>?>(e => e.Owner == "alice");
}

public class S2bContext : SparkContext
{
    public IQueryable<Employee> Employees => Session.Query<Employee>();
    public IQueryable<Vehicle> Vehicles => Session.Query<Vehicle>();
    public IQueryable<Company> Companies => Session.Query<Company>();
}

public abstract partial class S2bTests(string provider) : SparkTestDriver
{
    private static readonly Guid EmployeeTypeId = Guid.Parse("53620000-0000-4000-8000-000000000001");
    private static readonly Guid VehicleTypeId = Guid.Parse("53620000-0000-4000-8000-000000000002");
    private static readonly Guid CompanyTypeId = Guid.Parse("53620000-0000-4000-8000-000000000003");
    private static readonly Guid SkillTypeId = Guid.Parse("53620000-0000-4000-8000-000000000004");

    private static EntityAttributeDefinition A(string name, string type = "string", Action<EntityAttributeDefinition>? more = null)
    {
        var a = new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = name, DataType = type };
        more?.Invoke(a);
        return a;
    }

    private static EntityTypeFile Model<T>(Guid id, string breadcrumb, params EntityAttributeDefinition[] attributes) => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = id, Name = typeof(T).Name, ClrType = typeof(T).FullName!, Breadcrumb = breadcrumb, Attributes = attributes,
        },
    };

    private static EntityTypeFile[] Models() =>
    [
        Model<Company>(CompanyTypeId, "{Name}", A("Name")),
        Model<Skill>(SkillTypeId, "{Name}", A("Name")),
        Model<Vehicle>(VehicleTypeId, "{Plate}", A("Plate")),
        Model<Employee>(EmployeeTypeId, "{LastName}",
            A("FirstName"), A("LastName"), A("Owner"),
            A("Company", "Reference", a => a.ReferenceType = typeof(Company).FullName),
            A("Title", "TranslatedString"),
            A("Skills", "Reference", a => { a.ReferenceType = typeof(Skill).FullName; a.IsArray = true; })),
    ];

    private static EfSparkModel SparkModel()
    {
        var m = new EfSparkModel();
        m.Collections.Add((typeof(Company), "Companies", []));
        m.Collections.Add((typeof(Skill), "Skills", []));
        m.Collections.Add((typeof(Vehicle), "Vehicles", [typeof(Car), typeof(Truck)]));
        m.Collections.Add((typeof(Employee), "Employees", []));
        m.Joins.Add(new JoinCollection(typeof(Employee), nameof(Employee.Skills), typeof(Skill), "EmployeeSkills"));
        // S4: natural ids, a unique attribute, a length (S4Tests.cs).
        m.Collections.Add((typeof(Tag), "Tags", []));
        m.Configure = mb =>
        {
            mb.Entity<Tag>().Property(t => t.Name).HasMaxLength(20);
            mb.Entity<Tag>().HasIndex(t => t.Name).IsUnique();
        };
        return m;
    }

    private EfSparkStore efStore = null!;
    private SparkEndpointFactory<S2bContext> factory = null!;
    private IQueryExecutor executor = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        var db = "sparkspike536s2b";
        var builder = new DbContextOptionsBuilder();
        if (provider == "sqlserver")
            builder.UseSqlServer($@"Server=localhost\MSSQLSERVER01;Database={db};Trusted_Connection=True;TrustServerCertificate=True");
        else
            builder.UseNpgsql($"Host=127.0.0.1;Port=55432;Username=postgres;Password={Environment.GetEnvironmentVariable("SPARK536_PG_PASSWORD")};Database={db}");
        builder.Log();
        efStore = new EfSparkStore(builder.Options, SparkModel(), provider);

        await using (var ctx = efStore.NewContext())
        {
            await ctx.Database.EnsureDeletedAsync();
            await ctx.Database.EnsureCreatedAsync();
        }
        await SeedRelationalAsync();

        factory = new SparkEndpointFactory<S2bContext>(Store, Models(),
            configureServices: services =>
            {
                services.RemoveAll<ISparkStore>();
                services.RemoveAll<ISparkSession>();
                services.RemoveAll<ISparkQueryProvider>();
                services.AddSingleton<ISparkStore>(efStore);
                services.AddScoped<ISparkSession>(sp => sp.GetRequiredService<ISparkStore>().OpenSession());
                services.AddScoped<ISparkQueryProvider>(sp => new EfSparkQueryProvider(sp.GetRequiredService<ISparkSession>()));
            },
            security: SparkTestSecurity.Permissive);
        executor = factory.GetService<IQueryExecutor>();
        SqlLog.Clear();
    }

    public override async Task DisposeAsync()
    {
        // S2B_LOG=1: the SQL each test ran, appended to sql-<provider>.log next to the binaries.
        if (Environment.GetEnvironmentVariable("S2B_LOG") == "1")
            await File.AppendAllTextAsync($"sql-{provider}.log", $"\n===== {GetType().Name}\n{SqlLog.All()}\n");
        await factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private async Task SeedRelationalAsync()
    {
        using var s = (EfSparkSession)efStore.OpenSession();
        await s.StoreAsync(new Company { Name = "Acme" }, "companies/acme");
        await s.StoreAsync(new Skill { Name = "C#" }, "skills/csharp");
        await s.StoreAsync(new Skill { Name = "SQL" }, "skills/sql");
        await s.StoreAsync(new Car { Plate = "1-ABC-123", Seats = 5 }, "vehicles/car1");
        await s.StoreAsync(new Truck { Plate = "2-XYZ-999", Axles = 3 }, "vehicles/truck1");
        await s.StoreAsync(new Employee
        {
            FirstName = "Ada", LastName = "Lovelace", Owner = "alice", Company = "companies/acme",
            Title = TranslatedString.Create("Engineer"),
            Addresses = [new Address { Id = "home", Street = "Main 1", City = "London" }],
            Skills = ["skills/csharp", "skills/sql"],
        }, "employees/ada");
        await s.StoreAsync(new Employee { FirstName = "Grace", LastName = "Hopper", Owner = "alice", Skills = ["skills/sql"] }, "employees/grace");
        await s.StoreAsync(new Employee { FirstName = "Linus", LastName = "Torvalds", Owner = "bob", Skills = ["skills/csharp"] }, "employees/linus");
        await s.SaveChangesAsync();
    }

    private static SparkQuery Query(string source) => new() { Id = Guid.NewGuid(), Name = source, Source = $"Database.{source}" };

    private static string EmployeeSelects() => string.Join("\n---\n", SqlLog.Commands.Where(c => c.Contains("Employees") && c.Contains("SELECT")));

    [Fact]
    public async Task Row_filter_runs_as_sql()
    {
        var result = await executor.ExecuteQueryAsync(Query("Employees"));

        result.Items.Select(i => i.Id).Order().Should().BeEquivalentTo(["employees/ada", "employees/grace"]);
        result.TotalItems.Should().Be(2);
        EmployeeSelects().Should().Contain("'alice'");
    }

    [Fact]
    public async Task Restrict_to_ids_runs_as_sql()
    {
        var result = await executor.ExecuteQueryAsync(Query("Employees"), restrictToIds: ["employees/grace", "employees/linus"]);

        // linus is bob's: the row filter and the id restriction compose.
        result.Items.Select(i => i.Id).Should().BeEquivalentTo(["employees/grace"]);
        var sql = EmployeeSelects();
        // Parameterized, not literals (EF 11: one parameter per id, padded).
        sql.Should().NotContain("employees/linus");
        (sql.Contains("IN (@") || sql.Contains("OPENJSON") || sql.Contains("= ANY")).Should().BeTrue(sql);
    }

    [Fact]
    public async Task Search_runs_as_sql()
    {
        var result = await executor.ExecuteQueryAsync(Query("Employees"), search: "LOVE");

        result.Items.Select(i => i.Id).Should().BeEquivalentTo(["employees/ada"]);
        result.TotalItems.Should().Be(1);
        var sql = EmployeeSelects();
        sql.ToUpperInvariant().Should().Contain("LOWER(");
    }

    [Fact]
    public async Task Collection_column_filter_runs_over_the_join_table()
    {
        var result = await executor.ExecuteQueryAsync(Query("Employees"),
            columnFilters: [new QueryColumnFilter { Name = "Skills", Includes = ["skills/csharp"] }]);

        result.Items.Select(i => i.Id).Should().BeEquivalentTo(["employees/ada"]);
        EmployeeSelects().Should().Contain("EmployeeSkills");
    }

    [Fact]
    public async Task Derived_types_share_one_table()
    {
        var result = await executor.ExecuteQueryAsync(Query("Vehicles"));

        result.Items.Select(i => i.Id).Order().Should().BeEquivalentTo(["vehicles/car1", "vehicles/truck1"]);
        string.Join("\n", SqlLog.Commands).Should().Contain("Discriminator");
    }

    [Fact]
    public async Task Load_round_trips_json_column_child_table_and_join_list()
    {
        using var s = efStore.OpenSession();
        var ada = await s.LoadAsync<Employee>("employees/ada");

        ada.Should().NotBeNull();
        ada!.Title!.Translations["en"].Should().Be("Engineer");
        ada.Addresses.Select(a => a.City).Should().BeEquivalentTo(["London"]);
        ada.Skills.Should().BeEquivalentTo(["skills/csharp", "skills/sql"]);
    }

    [Fact]
    public async Task Stored_computed_member_is_written_recomputed_and_queryable()
    {
        using (var s = efStore.OpenSession())
        {
            var grace = (await s.LoadAsync<Employee>("employees/grace"))!;
            grace.FirstName = "Amazing Grace";
            await s.SaveChangesAsync();
        }

        SqlLog.Clear();
        using var check = efStore.OpenSession();
        var found = await check.ToListAsync(check.Query<Employee>().Where(e => e.FullName == "Amazing Grace Hopper"));

        found.Select(e => e.Id).Should().BeEquivalentTo(["employees/grace"]);
        SqlLog.All().Should().Contain("FullName");
    }
}

public class SqlServerS2bTests() : S2bTests("sqlserver");
public class PostgresS2bTests() : S2bTests("postgres");
