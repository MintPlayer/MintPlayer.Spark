using System.Net;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents;
using PO = MintPlayer.Spark.Abstractions.PersistentObject;

namespace MintPlayer.Spark.Tests.Endpoints.PersistentObject;

public class UpdateEndpointTests : SparkTestDriver
{
    private static readonly Guid PersonTypeId = Guid.Parse("55555555-dddd-dddd-dddd-555555555555");

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

    private static PO NewPerson(string id, string firstName, string lastName) => new()
    {
        Id = id,
        Name = "Person",
        ObjectTypeId = PersonTypeId,
        Attributes =
        [
            new() { Name = "FirstName", Value = firstName },
            new() { Name = "LastName", Value = lastName },
        ],
    };

    [Fact]
    public async Task Update_throws_404_when_entity_type_is_unknown()
    {
        var po = NewPerson("people/1", "A", "B");
        po.ObjectTypeId = Guid.NewGuid();  // force unknown type

        var ex = await Assert.ThrowsAsync<SparkClientException>(() => _client.UpdatePersistentObjectAsync(po));

        ex.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Update_throws_404_when_id_does_not_exist()
    {
        var po = NewPerson("people/does-not-exist", "A", "B");

        var ex = await Assert.ThrowsAsync<SparkClientException>(() => _client.UpdatePersistentObjectAsync(po));

        ex.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Update_modifies_existing_document()
    {
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new Person { FirstName = "Alice", LastName = "Smith" }, "people/1");
            await session.SaveChangesAsync();
        }

        var saved = await _client.UpdatePersistentObjectAsync(NewPerson("people/1", "Alicia", "Smith-Jones"));
        saved.Should().NotBeNull();

        WaitForIndexing(Store);
        using var verify = Store.OpenAsyncSession();
        var stored = await verify.LoadAsync<Person>("people/1");
        stored.FirstName.Should().Be("Alicia");
        stored.LastName.Should().Be("Smith-Jones");
    }
}
