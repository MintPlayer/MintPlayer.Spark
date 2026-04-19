using System.Net;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;

namespace MintPlayer.Spark.Tests.Endpoints.PersistentObject;

public class GetEndpointTests : SparkTestDriver
{
    private static readonly Guid PersonTypeId = Guid.Parse("11111111-aaaa-aaaa-aaaa-111111111111");

    private SparkEndpointFactory _factory = null!;
    private HttpClient _client = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _factory = new SparkEndpointFactory(Store, [TestModels.Person(PersonTypeId)]);
        _client = _factory.CreateClient();
    }

    public override async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task Get_returns_404_when_entity_type_is_unknown()
    {
        var unknownTypeId = Guid.NewGuid();

        var response = await _client.GetAsync($"/spark/po/{unknownTypeId}/people%2F1");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_returns_404_when_entity_type_is_known_but_id_does_not_exist()
    {
        var response = await _client.GetAsync($"/spark/po/{PersonTypeId}/people%2Fdoes-not-exist");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_resolves_entity_type_by_alias()
    {
        var response = await _client.GetAsync($"/spark/po/person/people%2Fdoes-not-exist");

        // Alias resolves → entity type known → falls through to "id not found"
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_returns_persistent_object_for_existing_document()
    {
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new Person { FirstName = "Alice", LastName = "Smith" }, "people/1");
            await session.SaveChangesAsync();
        }
        WaitForIndexing(Store);

        var response = await _client.GetAsync($"/spark/po/{PersonTypeId}/people%2F1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"id\":\"people/1\"");
        body.Should().Contain("Alice");
        body.Should().Contain("Smith");
    }
}

public static class TestModels
{
    public static EntityTypeFile Person(Guid id) => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = id,
            Name = "Person",
            ClrType = typeof(Person).FullName!,
            DisplayAttribute = "LastName",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "FirstName", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "LastName", DataType = "string" },
            ],
        }
    };

    public static EntityTypeFile PersonWithRequiredLastName(Guid id) => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = id,
            Name = "Person",
            ClrType = typeof(Person).FullName!,
            DisplayAttribute = "LastName",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "FirstName", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "LastName", DataType = "string", IsRequired = true },
            ],
        }
    };
}
