// #536 spike S5 (PRD D32): one neutral view declaration (S5Views.cs) on SQL Server, PostgreSQL, LINQ to objects
// (InMemory) and RavenDB; the executor queries it by name; synchronize's migration writes and maintains its SQL.
using System.Collections;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Design;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;

namespace S2b;

public abstract partial class S2bTests
{
    private static readonly (string Id, string FullName, string? Company, int Skills)[] Expected =
    [
        ("employees/ada", "Ada Lovelace", "Acme", 2),
        ("employees/grace", "Grace Hopper", null, 1),
        ("employees/linus", "Linus Torvalds", null, 1),
    ];

    private static IEnumerable<(string, string, string?, int)> Shape(IEnumerable<VEmployee> rows)
        => rows.OrderBy(r => r.Id, StringComparer.Ordinal).Select(r => (r.Id!, r.FullName, r.CompanyName, r.SkillCount));

    private string ViewSql => efStore.Model.ViewSql[nameof(EmployeesWithCompany)];

    [Fact]
    public async Task S5_EF_writes_the_view_sql_per_dialect()
    {
        if (Environment.GetEnvironmentVariable("S2B_LOG") == "1")
            await File.WriteAllTextAsync($"view-{provider}.sql", ViewSql);

        ViewSql.Should().Contain("Companies");
        ViewSql.Should().Contain("EmployeeSkills");
        // A view body takes no parameters: every value in the declaration is a literal.
        ViewSql.Should().NotContain("DECLARE");
        ViewSql.Should().NotContain("@");
    }

    [Fact]
    public async Task S5_the_view_is_a_set_through_the_session()
    {
        using var s = efStore.OpenSession();
        var rows = await s.ToListAsync(s.Query<VEmployee, EmployeesWithCompany>());

        Shape(rows).Should().BeEquivalentTo(Expected);
        string.Join("\n", SqlLog.Commands).Should().Contain(nameof(EmployeesWithCompany));
    }

    [Fact]
    public async Task S5_a_committed_write_is_in_the_view_at_once()
    {
        using (var s = efStore.OpenSession())
        {
            var grace = (await s.LoadAsync<Employee>("employees/grace"))!;
            grace.Company = "companies/acme";
            await s.SaveChangesAsync();
        }
        using var check = efStore.OpenSession();
        var row = (await check.ToListAsync(check.Query<VEmployee, EmployeesWithCompany>().Where(v => v.Id == "employees/grace"))).Single();

        row.CompanyName.Should().Be("Acme");
    }

    [Fact]
    public void S5_the_same_declaration_runs_as_linq_to_objects()
    {
        var acme = new Company { Id = "companies/acme", Name = "Acme" };
        var rows = new Dictionary<Type, IList>
        {
            [typeof(Company)] = new List<Company> { acme },
            [typeof(Employee)] = new List<Employee>
            {
                new() { Id = "employees/ada", FirstName = "Ada", LastName = "Lovelace", Owner = "alice", Company = acme.Id, Skills = ["skills/csharp", "skills/sql"] },
                new() { Id = "employees/grace", FirstName = "Grace", LastName = "Hopper", Owner = "alice", Skills = ["skills/sql"] },
                new() { Id = "employees/linus", FirstName = "Linus", LastName = "Torvalds", Owner = "bob", Skills = ["skills/csharp"] },
            },
        };

        Shape(InMemoryViewRenderer.Run(new EmployeesWithCompany(), rows)).Should().BeEquivalentTo(Expected);
    }

    [Fact]
    public async Task S5_the_same_declaration_deploys_as_a_ravendb_index()
    {
        using (var rs = Store.OpenAsyncSession())
        {
            await rs.StoreAsync(new Company { Name = "Acme" }, "companies/acme");
            await rs.StoreAsync(new Employee { FirstName = "Ada", LastName = "Lovelace", Owner = "alice", Company = "companies/acme", Skills = ["skills/csharp", "skills/sql"] }, "employees/ada");
            await rs.StoreAsync(new Employee { FirstName = "Grace", LastName = "Hopper", Owner = "alice", Skills = ["skills/sql"] }, "employees/grace");
            await rs.StoreAsync(new Employee { FirstName = "Linus", LastName = "Torvalds", Owner = "bob", Skills = ["skills/csharp"] }, "employees/linus");
            await rs.SaveChangesAsync();
        }

        var index = RavenViewRenderer.Render<Employee, VEmployee>(new EmployeesWithCompany());
        var definition = index.CreateIndexDefinition();
        if (Environment.GetEnvironmentVariable("S2B_LOG") == "1")
            await File.WriteAllTextAsync("view-ravendb.txt", string.Join("\n", definition.Maps));
        await index.ExecuteAsync(Store);
        WaitForIndexing(Store);

        using var q = Store.OpenAsyncSession();
        var rows = await Raven.Client.Documents.LinqExtensions.ToListAsync(q.Query<VEmployee>(index.IndexName).ProjectInto<VEmployee>());

        string.Join("\n", definition.Maps).Should().Contain("LoadDocument");
        Shape(rows).Should().BeEquivalentTo(Expected);
    }

    [Fact]
    public async Task S5_the_executor_queries_the_view_by_index_name()
    {
        var query = new SparkQuery { Id = Guid.NewGuid(), Name = "Overview", Source = "Database.Employees", IndexName = nameof(EmployeesWithCompany) };

        var all = await executor.ExecuteQueryAsync(query);
        var acme = await executor.ExecuteQueryAsync(query, search: "acme");

        // The row filter (Owner == alice) rebinds onto VEmployee by member name; search reaches a joined column.
        all.Items.Select(i => i.Id).Order().Should().BeEquivalentTo(["employees/ada", "employees/grace"]);
        acme.Items.Select(i => i.Id).Should().BeEquivalentTo(["employees/ada"]);
        var sql = string.Join("\n", SqlLog.Commands.Where(c => c.Contains(nameof(EmployeesWithCompany))));
        sql.Should().Contain("'alice'");
        sql.ToUpperInvariant().Should().Contain("LOWER(");
    }

    [Fact]
    public async Task S5_the_initial_migration_creates_the_view_and_the_snapshot_keeps_its_sql()
    {
        await using var ctx = efStore.NewContext();
        var (scaffolder, sp) = Scaffolder(ctx);
        await using var _ = sp;
        var migration = scaffolder.ScaffoldMigration("Initial", "S2b", "Migrations", "C#");
        if (Environment.GetEnvironmentVariable("S2B_LOG") == "1")
        {
            await File.WriteAllTextAsync($"migration-{provider}.cs.txt", migration.MigrationCode);
            await File.WriteAllTextAsync($"snapshot-{provider}.cs.txt", migration.SnapshotCode);
        }

        var parts = migration.MigrationCode.Split("protected override void Down");
        parts[0].Should().Contain("migrationBuilder.Sql(");
        parts[0].Should().Contain("CREATE VIEW");
        // Created after the tables it reads.
        parts[0].IndexOf("CREATE VIEW", StringComparison.Ordinal).Should().BeGreaterThan(parts[0].LastIndexOf("migrationBuilder.CreateTable(", StringComparison.Ordinal));
        parts[1].Should().Contain("DROP VIEW");
        parts[1].IndexOf("DROP VIEW", StringComparison.Ordinal).Should().BeLessThan(parts[1].IndexOf("migrationBuilder.DropTable(", StringComparison.Ordinal));
        migration.SnapshotCode.Should().Contain(SparkViewDiffer.SqlAnnotation);
    }

    [Fact]
    public async Task S5_an_unchanged_model_has_no_pending_change()
    {
        await using var a = efStore.NewContext();
        await using var b = new EfSparkStore(Options(), SparkModel(), provider).NewContext();
        var differ = a.GetService<IMigrationsModelDiffer>();

        differ.HasDifferences(Relational(a), Relational(b)).Should().BeFalse();
    }

    [Fact]
    public async Task S5_a_changed_view_is_dropped_and_recreated_and_nothing_else()
    {
        await using var v1 = efStore.NewContext();
        await using var v2 = new EfSparkStore(Options(), SparkModel(new EmployeesWithCompanyV2()), provider).NewContext();
        var operations = v1.GetService<IMigrationsModelDiffer>().GetDifferences(Relational(v1), Relational(v2));

        operations.Select(o => ((SqlOperation)o).Sql.Split(' ')[0] + " " + ((SqlOperation)o).Sql.Split(' ')[1]).Should().BeEquivalentTo(["DROP VIEW", "CREATE VIEW"]);
        await ApplyAsync(v2, operations);

        using var s = efStore.OpenSession();
        var ada = (await s.ToListAsync(s.Query<VEmployee, EmployeesWithCompany>().Where(v => v.Id == "employees/ada"))).Single();
        ada.FullName.Should().Be("Lovelace, Ada");
    }

    [Fact]
    public async Task S5_a_table_change_under_a_view_recreates_the_view_around_it()
    {
        await using var v1 = efStore.NewContext();
        await using var v2 = new EfSparkStore(Options(), SparkModel(variant: "LastName80"), provider).NewContext();
        var operations = v1.GetService<IMigrationsModelDiffer>().GetDifferences(Relational(v1), Relational(v2));
        var alter = operations.OfType<AlterColumnOperation>().Single();

        alter.Name.Should().Be(nameof(Employee.LastName));
        operations[0].Should().BeOfType<SqlOperation>();
        ((SqlOperation)operations[0]).Sql.Should().StartWith("DROP VIEW");
        ((SqlOperation)operations[^1]).Sql.Should().StartWith("CREATE VIEW");

        // Why: the ALTER alone, with the view in place.
        string? alone = null;
        try { await ApplyAsync(v2, [alter]); alone = "accepted"; }
        catch (Exception ex) { alone = ex.Message.Split('\n')[0]; }
        if (Environment.GetEnvironmentVariable("S2B_LOG") == "1")
            await File.WriteAllTextAsync($"alter-under-view-{provider}.txt", alone);
        if (provider == "postgres") alone.Should().Contain("view");

        // Undo whatever the probe did, then run the generated sequence on the v1 schema.
        await using (var fresh = efStore.NewContext()) await CreateFromNothingAsync(fresh);
        await ApplyAsync(v2, operations);
        using var s = efStore.OpenSession();
        (await s.ToListAsync(s.Query<VEmployee, EmployeesWithCompany>())).Should().HaveCount(0); // recreated empty database
    }

    private static IRelationalModel Relational(DbContext ctx) => ctx.GetService<IDesignTimeModel>().Model.GetRelationalModel();

    // From spike S3: design-time services from public API only.
    private static (IMigrationsScaffolder, ServiceProvider) Scaffolder(DbContext db)
    {
        var services = new ServiceCollection();
        services.AddEntityFrameworkDesignTimeServices(new SilentReporter());
        services.AddDbContextDesignTimeServices(db);
        var providerAssembly = Assembly.Load(db.Database.ProviderName!);
        var attr = providerAssembly.GetCustomAttribute<DesignTimeProviderServicesAttribute>()!;
        ((IDesignTimeServices)Activator.CreateInstance(providerAssembly.GetType(attr.TypeName, throwOnError: true)!)!).ConfigureDesignTimeServices(services);
        var sp = services.BuildServiceProvider();
        return (sp.GetRequiredService<IMigrationsScaffolder>(), sp);
    }

    private sealed class SilentReporter : IOperationReporter
    {
        public void WriteError(string message) { }
        public void WriteWarning(string message) { }
        public void WriteInformation(string message) { }
        public void WriteVerbose(string message) { }
    }
}

/// <summary>The catalog entry a neutral view gets: name, declaring type, collection, row type.</summary>
public sealed class SpikeIndexCatalog : IIndexCatalog
{
    private readonly IndexCatalogEntry entry;

    public SpikeIndexCatalog(Type viewType, Type collection, Type rowType)
    {
        entry = new IndexCatalogEntry { IndexName = viewType.Name, IndexType = viewType, CollectionType = collection };
        typeof(IndexCatalogEntry).GetProperty(nameof(IndexCatalogEntry.ProjectionType))!.SetValue(entry, rowType);
    }

    public void RegisterIndex(Type indexType) { }
    public void RegisterProjection(Type projectionType, Type indexType) { }
    public void Freeze() { }
    public void RegisterAssemblies(IReadOnlyList<Assembly> assemblies) { }
    public IReadOnlySet<string>? GetQuerySurface(IndexCatalogEntry entry) => null;
    public void BuildDefinition(IndexCatalogEntry entry) { }
    public IndexCatalogEntry? GetByIndexName(string indexName) => string.Equals(indexName, entry.IndexName, StringComparison.OrdinalIgnoreCase) ? entry : null;
    public IndexCatalogEntry? GetDefaultFor(Type entityType) => null;
    public IEnumerable<IndexCatalogEntry> GetAllEntries() => [entry];
}
