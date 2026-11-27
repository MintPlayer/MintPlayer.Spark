using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Operations.Indexes;
using IdCompany = MintPlayer.Spark.Tests.Storage.SparkIdConformanceTests.IdCompany;
using IdPerson = MintPlayer.Spark.Tests.Storage.SparkIdConformanceTests.IdPerson;

namespace MintPlayer.Spark.Tests.Storage;

/// <summary>
/// RavenDB static indexes over typed-id entities (#536 D23, spike S-ID6, PRD §8.13). Without
/// <see cref="RavenIndexIdRewriter"/> the translator writes the own id as a body field (absent: null) and a reference's
/// <c>.Value</c> as a member of the stored string (the index errors on every document). Spark's index bases rewrite
/// every map, so authors write typed ids as they would anywhere else.
/// </summary>
public sealed class RavenTypedIdIndexTests : IAsyncLifetime
{
    private readonly Driver driver = new();

    public Task InitializeAsync() => driver.InitializeAsync();

    public Task DisposeAsync() => driver.DisposeAsync();

    public sealed class PersonRow
    {
        public Id<IdPerson> Id { get; set; }
        public string? PersonId { get; set; }
        public Id<IdCompany>? CompanyId { get; set; }
        public string? CompanyName { get; set; }
    }

    /// <summary>The <c>.Value</c> forms, a nullable reference's included.</summary>
    public sealed class People_ByValue : SparkIndexCreationTask<IdPerson>
    {
        public People_ByValue()
        {
            Map = people => from p in people
                            let c = LoadDocument<IdCompany>(p.CompanyId!.Value.Value)
                            select new PersonRow { Id = p.Id, PersonId = p.Id.Value, CompanyId = p.CompanyId, CompanyName = c.Name };
            StoreAllFields(FieldStorage.Yes);
        }
    }

    /// <summary>The <c>ToString()</c> forms.</summary>
    public sealed class People_ByText : SparkIndexCreationTask<IdPerson>
    {
        public People_ByText()
        {
            Map = people => from p in people
                            let c = LoadDocument<IdCompany>(p.CompanyId.ToString())
                            select new PersonRow { Id = p.Id, PersonId = p.Id.ToString(), CompanyId = p.CompanyId, CompanyName = c.Name };
            StoreAllFields(FieldStorage.Yes);
        }
    }

    /// <summary>The multi-map base rewrites <c>AddMap</c>.</summary>
    public sealed class People_MultiMap : SparkMultiMapIndexCreationTask<PersonRow>
    {
        public People_MultiMap()
        {
            AddMap<IdPerson>(people => from p in people
                                       let c = LoadDocument<IdCompany>(p.CompanyId!.Value.Value)
                                       select new PersonRow { Id = p.Id, PersonId = p.Id.Value, CompanyId = p.CompanyId, CompanyName = c.Name });
            StoreAllFields(FieldStorage.Yes);
        }
    }

    public static TheoryData<string> Indexes => [nameof(People_ByValue), nameof(People_ByText), nameof(People_MultiMap)];

    private static AbstractIndexCreationTask Create(string name) => name switch
    {
        nameof(People_ByValue) => new People_ByValue(),
        nameof(People_ByText) => new People_ByText(),
        _ => new People_MultiMap(),
    };

    [Theory]
    [MemberData(nameof(Indexes))]
    public async Task An_index_over_typed_ids_reads_the_document_id_and_loads_the_reference(string indexName)
    {
        var store = driver.DocumentStore;
        var index = Create(indexName);
        index.Conventions = store.Conventions;
        var maps = string.Join("\n", index.CreateIndexDefinition().Maps);
        maps.Should().Contain("Id(").And.NotContain(".Id.Value").And.NotContain("CompanyId.Value");
        await index.ExecuteAsync(store);

        var acme = new IdCompany { Id = Id<IdCompany>.FromTrusted("idcompanies/acme"), Name = "Acme" };
        var ann = new IdPerson { Id = Id<IdPerson>.FromTrusted("idpeople/ann"), Name = "Ann", CompanyId = acme.Id };
        using (var session = store.OpenAsyncSession())
        {
            await session.StoreAsync(acme);
            await session.StoreAsync(ann);
            await session.SaveChangesAsync();
        }

        using var read = store.OpenAsyncSession();
        var rows = await read.Query<PersonRow>(index.IndexName)
            .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(30)))
            .Where(r => r.CompanyId == acme.Id)
            .ProjectInto<PersonRow>()
            .ToListAsync();

        var errors = await store.Maintenance.SendAsync(new GetIndexErrorsOperation([index.IndexName]));
        errors.SelectMany(e => e.Errors).Should().BeEmpty();
        var row = rows.Should().ContainSingle().Which;
        row.Id.Should().Be(ann.Id);
        row.PersonId.Should().Be("idpeople/ann");
        row.CompanyId.Should().Be(acme.Id);
        row.CompanyName.Should().Be("Acme");
    }

    private sealed class Driver : RavenSparkTestDriver
    {
        public IDocumentStore DocumentStore => Store;
    }
}
