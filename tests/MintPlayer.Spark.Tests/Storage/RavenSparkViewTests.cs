using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Indexes;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Storage.Raven;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using static MintPlayer.Spark.Tests.Storage.SparkViewTests;

namespace MintPlayer.Spark.Tests.Storage;

/// <summary>
/// #536 D32 / S5 on RavenDB: the same <see cref="EmployeesWithCompany"/> declaration the in-memory test renders, deployed
/// as a static index of its name and queried through the provider, with the same rows.
/// </summary>
public class RavenSparkViewTests : IAsyncLifetime
{
    private readonly Driver driver = new();

    public Task InitializeAsync() => driver.InitializeAsync();

    public Task DisposeAsync() => driver.DisposeAsync();

    [Fact]
    public async Task A_view_deploys_as_a_static_index_and_reads_the_same_rows()
    {
        var store = new RavenSparkStore(driver.DocumentStore);
        using (var seed = store.OpenSession())
        {
            await seed.StoreAsync(new ViewCompany { Id = Id<ViewCompany>.FromTrusted("viewcompanies/1"), Name = "Acme" });
            await seed.StoreAsync(new ViewEmployee { Id = Id<ViewEmployee>.FromTrusted("viewemployees/1"), FirstName = "Ada", LastName = "Lovelace", Company = "ViewCompanies/1", Employer = Id<ViewCompany>.FromTrusted("viewcompanies/1"), Skills = ["math", "poetry"] });
            await seed.StoreAsync(new ViewEmployee { Id = Id<ViewEmployee>.FromTrusted("viewemployees/2"), FirstName = "Alan", LastName = "Turing", Company = "viewcompanies/404" });
            await seed.SaveChangesAsync();
        }

        new RavenIndexDeployment(driver.DocumentStore, NullLogger<RavenIndexDeployment>.Instance).Deploy([typeof(SparkViewTests).Assembly], t => t == typeof(EmployeesWithCompany));
        driver.WaitForIndexes();

        using var session = store.OpenSession();
        var provider = TestQueryProvider.For(session);
        var rows = (await provider.ToListAsync(
                provider.Project(provider.OpenIndex(typeof(ViewEmployee), typeof(EmployeesWithCompany), false), typeof(VEmployee)), default))
            .Cast<VEmployee>().OrderBy(r => r.FullName).ToList();

        rows.Should().HaveCount(2);
        rows[0].FullName.Should().Be("Ada Lovelace");
        rows[0].CompanyName.Should().Be("Acme");
        rows[0].EmployerName.Should().Be("Acme", "Load takes an Id<T> as its stored text (S-ID6)");
        rows[0].SkillCount.Should().Be(2);
        rows[1].CompanyName.Should().BeNull("a missing reference loads as null");
        rows[1].EmployerName.Should().BeNull();
    }

    /// <summary>
    /// #716: a class-based index <c>Employee_Names</c> was deployed as <c>Employee/Names</c>, and RavenDB keeps both names
    /// in one folder, <c>Employee_Names</c>. When a view of that name replaces the class, the server refuses the view while
    /// the old index exists ("would result in directory name collision"), so deployment removes the old index first.
    /// </summary>
    [Fact]
    public async Task A_view_replaces_the_class_based_index_it_superseded()
    {
        await driver.DocumentStore.Maintenance.SendAsync(new PutIndexesOperation(new IndexDefinition
        {
            Name = "Employee/Names",
            Maps = ["from e in docs.ViewEmployees select new { e.LastName }"],
        }));

        new RavenIndexDeployment(driver.DocumentStore, NullLogger<RavenIndexDeployment>.Instance)
            .Deploy([typeof(SparkViewTests).Assembly], t => t == typeof(EmployeeNames) || t == typeof(EmployeesWithCompany));

        var names = await driver.DocumentStore.Maintenance.SendAsync(new GetIndexNamesOperation(0, int.MaxValue));
        names.Should().Contain("Employee_Names");
        names.Should().Contain(nameof(EmployeesWithCompany));
        names.Should().NotContain("Employee/Names");
    }

    /// <summary>
    /// #716: the views were deployed in one batch, which RavenDB applies all or nothing, so one refused view kept every
    /// other view out of the database (messaging's <c>SparkMessages_ByState</c> among them). Each view now deploys on its
    /// own, and a refusal is logged as an error. An index locked with <see cref="IndexLockMode.LockedError"/> refuses the
    /// changed definition.
    /// </summary>
    [Fact]
    public async Task A_view_the_server_refuses_keeps_no_other_view_out()
    {
        await driver.DocumentStore.Maintenance.SendAsync(new PutIndexesOperation(new IndexDefinition
        {
            Name = nameof(EmployeesWithCompany),
            Maps = ["from e in docs.ViewEmployees select new { e.FirstName }"],
            LockMode = IndexLockMode.LockedError,
        }));
        var log = new RecordingLogger();

        new RavenIndexDeployment(driver.DocumentStore, log)
            .Deploy([typeof(SparkViewTests).Assembly], t => t == typeof(EmployeeNames) || t == typeof(EmployeesWithCompany));

        var names = await driver.DocumentStore.Maintenance.SendAsync(new GetIndexNamesOperation(0, int.MaxValue));
        names.Should().Contain("Employee_Names");
        log.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Message.Contains(nameof(EmployeesWithCompany)));
    }

    [Fact]
    public void The_catalog_enters_a_view_unless_a_native_index_of_its_name_shadows_it()
    {
        var catalog = new IndexCatalog();
        catalog.RegisterAssemblies([typeof(SparkViewTests).Assembly]);

        var entry = catalog.GetByIndexName(nameof(EmployeesWithCompany))!;
        entry.IndexType.Should().Be(typeof(EmployeesWithCompany));
        entry.ProjectionType.Should().Be(typeof(VEmployee));
        catalog.BuildDefinition(entry);

        var definition = IndexCatalog.TaskFor(typeof(EmployeesWithCompany))!.CreateIndexDefinition();
        definition.Name.Should().Be(nameof(EmployeesWithCompany));
        definition.Maps.Should().ContainSingle().Which.Should().Contain("LoadDocument").And.NotContain("Id =");
    }

    [Fact]
    public void The_catalog_elects_no_view_it_does_not_claim()
    {
        var catalog = new IndexCatalog();
        catalog.RegisterIndex(typeof(EmployeesWithCompany));
        catalog.RegisterIndex(typeof(EmployeeDetails));

        ((Action)catalog.Freeze).Should().NotThrow(
            "every view has a row type, so having one chooses nothing: a view is a default only by [DefaultIndex] (#536 D48)");
        catalog.GetDefaultFor(typeof(ViewEmployee)).Should().BeNull();
    }

    private sealed class Driver : RavenSparkTestDriver
    {
        public IDocumentStore DocumentStore => Store;

        public void WaitForIndexes() => WaitForIndexing(Store);
    }

    private sealed class RecordingLogger : ILogger<RavenIndexDeployment>
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Enqueue((logLevel, formatter(state, exception)));
    }
}
