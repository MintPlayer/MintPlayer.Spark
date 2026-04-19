using System.Net;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Endpoints.PersistentObject;

public class UpdateEndpointTests : SparkTestDriver
{
    private static readonly Guid PersonTypeId = Guid.Parse("55555555-dddd-dddd-dddd-555555555555");

    private SparkEndpointFactory _factory = null!;
    private SparkTestClient _client = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _factory = new SparkEndpointFactory(Store, [TestModels.Person(PersonTypeId)]);
        _client = await _factory.CreateAuthorizedClientAsync();
    }

    public override async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private static object UpdatePersonRequest(string firstName, string lastName) => new
    {
        persistentObject = new
        {
            name = "Person",
            objectTypeId = PersonTypeId,
            attributes = new[]
            {
                new { name = "FirstName", value = (object)firstName },
                new { name = "LastName", value = (object)lastName },
            }
        }
    };

    [Fact]
    public async Task Update_returns_404_when_entity_type_is_unknown()
    {
        var unknownTypeId = Guid.NewGuid();

        var response = await _client.PutJsonAsync($"/spark/po/{unknownTypeId}/people%2F1", UpdatePersonRequest("A", "B"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Update_returns_404_when_id_does_not_exist()
    {
        var response = await _client.PutJsonAsync(
            $"/spark/po/{PersonTypeId}/people%2Fdoes-not-exist",
            UpdatePersonRequest("A", "B"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Update_modifies_existing_document_and_returns_200()
    {
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new Person { FirstName = "Alice", LastName = "Smith" }, "people/1");
            await session.SaveChangesAsync();
        }

        var response = await _client.PutJsonAsync(
            $"/spark/po/{PersonTypeId}/people%2F1",
            UpdatePersonRequest("Alicia", "Smith-Jones"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        WaitForIndexing(Store);
        using var verify = Store.OpenAsyncSession();
        var stored = await verify.LoadAsync<Person>("people/1");
        stored.FirstName.Should().Be("Alicia");
        stored.LastName.Should().Be("Smith-Jones");
    }
}
