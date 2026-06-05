using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;

namespace MintPlayer.Spark.Tests.Endpoints.PersistentObject;

public class GetEndpointTests : SparkTestDriver
{
    private static readonly Guid PersonTypeId = Guid.Parse("11111111-aaaa-aaaa-aaaa-111111111111");

    private SparkEndpointFactory _factory = null!;
    private SparkClient _client = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _factory = new SparkEndpointFactory(Store, [TestModels.Person(PersonTypeId)]);
        _client = new SparkClient(_factory.CreateClient(), ownsClient: true);
    }

    public override async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task Get_returns_null_when_entity_type_is_unknown()
    {
        var unknownTypeId = Guid.NewGuid();

        var result = await _client.GetPersistentObjectAsync(unknownTypeId, "people/1");

        result.Should().BeNull();
    }

    [Fact]
    public async Task Get_returns_null_when_entity_type_is_known_but_id_does_not_exist()
    {
        var result = await _client.GetPersistentObjectAsync(PersonTypeId, "people/does-not-exist");

        result.Should().BeNull();
    }

    [Fact]
    public async Task Get_resolves_entity_type_by_alias()
    {
        // Alias resolves → entity type known → falls through to "id not found" → null.
        var result = await _client.GetPersistentObjectAsync("person", "people/does-not-exist");

        result.Should().BeNull();
    }

    [Fact]
    public async Task Get_returns_persistent_object_for_existing_document()
    {
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new Person { FirstName = "Alice", LastName = "Smith" }, "people/1");
            await session.SaveChangesAsync();
        }
        await RavenIndexHelper.WaitForNonStaleAsync(Store);

        var po = await _client.GetPersistentObjectAsync(PersonTypeId, "people/1");

        po.Should().NotBeNull();
        po!.Id.Should().Be("people/1");
        // Value deserializes as JsonElement when the declared type is object?, so compare via
        // ToString() which works for both a plain string and a JsonElement string.
        po.Attributes.Should().Contain(a => a.Name == "FirstName" && a.Value!.ToString() == "Alice");
        po.Attributes.Should().Contain(a => a.Name == "LastName" && a.Value!.ToString() == "Smith");
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
