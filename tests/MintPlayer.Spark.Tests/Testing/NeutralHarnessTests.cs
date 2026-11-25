using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Testing;
using PO = MintPlayer.Spark.Abstractions.PersistentObject;
using POA = MintPlayer.Spark.Abstractions.PersistentObjectAttribute;

namespace MintPlayer.Spark.Tests.Testing;

/// <summary>
/// #536 M5 (D35): one class on the neutral driver, with no line of provider code. It runs on the database
/// <c>SPARK_TEST_DATABASE</c> picks: this assembly references RavenDB, SQL Server, PostgreSQL and InMemory.
/// </summary>
public class NeutralHarnessTests : SparkTestDriver
{
    private static readonly Guid CarTypeId = Guid.Parse("536d3500-0000-4000-8000-000000000001");
    private static readonly Guid CarsQueryId = Guid.Parse("536d3500-0000-4000-8000-000000000002");

    public sealed class HarnessContext : SparkContext
    {
        public IQueryable<HarnessCar> Cars => Session.Query<HarnessCar>();
    }

    public sealed class HarnessCar
    {
        public Id<HarnessCar> Id { get; set; }
        public string Plate { get; set; } = "";
        public string Color { get; set; } = "";

        // What the document-id generator emits (D23); this project runs no generator.
        internal string? __DocumentId { get => Id.IsEmpty ? null : Id.Value; set => Id = Id<HarnessCar>.FromTrusted(value); }
    }

    private static EntityTypeFile Model() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = CarTypeId,
            Name = "HarnessCar",
            ClrType = typeof(HarnessCar).FullName!,
            Breadcrumb = "{Plate}",
            Attributes =
            [
                new() { Id = Guid.Parse("536d3500-0000-4000-8000-000000000011"), Name = "Plate", DataType = "string", Order = 1 },
                new() { Id = Guid.Parse("536d3500-0000-4000-8000-000000000012"), Name = "Color", DataType = "string", Order = 2 },
            ],
        },
        Queries = [new SparkQuery { Id = CarsQueryId, Name = "HarnessCars", Source = "Database.Cars", EntityType = "HarnessCar" }],
    };

    private SparkEndpointFactory<HarnessContext> NewHost() => new(Database, [Model()]);

    private static PO NewCar(string plate, string color) => new()
    {
        Name = "HarnessCar",
        ObjectTypeId = CarTypeId,
        Attributes = [new POA { Name = "Plate", Value = plate }, new POA { Name = "Color", Value = color }],
    };

    private static string? ValueOf(PO po, string name) => po.Attributes.Single(a => a.Name == name).Value?.ToString();

    [Fact]
    public async Task An_object_is_created_read_updated_and_deleted_through_the_endpoints()
    {
        await using var host = NewHost();
        using var client = new SparkClient(host.CreateClient(), ownsClient: true);

        var created = await client.CreatePersistentObjectAsync(NewCar("1-ABC-123", "red"));
        created.Id.Should().NotBeNullOrEmpty();

        var loaded = await client.GetPersistentObjectAsync(CarTypeId, created.Id!);
        ValueOf(loaded!, "Plate").Should().Be("1-ABC-123");

        loaded!.Attributes.Single(a => a.Name == "Color").Value = "blue";
        await client.UpdatePersistentObjectAsync(loaded);
        var updated = await client.GetPersistentObjectAsync(CarTypeId, created.Id!);
        ValueOf(updated!, "Color").Should().Be("blue");

        await client.DeletePersistentObjectAsync(updated!);
        (await client.GetPersistentObjectAsync(CarTypeId, created.Id!)).Should().BeNull();
    }

    [Fact]
    public async Task Seeded_rows_are_listed_by_the_query()
    {
        await using var host = NewHost();
        var ids = host.GetService<ISparkIds>();
        await host.SeedAsync(async session =>
        {
            await session.StoreAsync(new HarnessCar { Id = await ids.NewAsync<HarnessCar>(), Plate = "2-DEF-456", Color = "green" });
            await session.StoreAsync(new HarnessCar { Id = await ids.NewAsync<HarnessCar>(), Plate = "3-GHI-789", Color = "black" });
        });

        using var client = new SparkClient(host.CreateClient(), ownsClient: true);
        var result = await client.ExecuteQueryAsync(CarsQueryId);

        result.Items.Select(i => i.Values.Single(v => v.Key == "Plate").Value?.ToString())
            .Should().BeEquivalentTo(["2-DEF-456", "3-GHI-789"]);
    }

    [Fact]
    public async Task A_later_host_on_the_same_database_reads_what_an_earlier_one_wrote()
    {
        string id;
        await using (var first = NewHost())
        {
            using var client = new SparkClient(first.CreateClient(), ownsClient: true);
            id = (await client.CreatePersistentObjectAsync(NewCar("4-JKL-012", "white"))).Id!;
        }

        await using var second = NewHost();
        using var reader = new SparkClient(second.CreateClient(), ownsClient: true);
        var loaded = await reader.GetPersistentObjectAsync(CarTypeId, id);
        ValueOf(loaded!, "Plate").Should().Be("4-JKL-012");
    }
}
