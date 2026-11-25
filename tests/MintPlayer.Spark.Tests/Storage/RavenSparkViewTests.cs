using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Indexes;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Storage.Raven;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents;
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

        new RavenIndexDeployment(driver.DocumentStore).Deploy([typeof(SparkViewTests).Assembly], t => t == typeof(EmployeesWithCompany));
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

    private sealed class Driver : SparkTestDriver
    {
        public IDocumentStore DocumentStore => Store;

        public void WaitForIndexes() => WaitForIndexing(Store);
    }
}
