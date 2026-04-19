using System.Net;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Endpoints.PersistentObject;

public class DeleteEndpointTests : SparkTestDriver
{
    private static readonly Guid PersonTypeId = Guid.Parse("66666666-eeee-eeee-eeee-666666666666");

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

    [Fact]
    public async Task Delete_returns_404_when_entity_type_is_unknown()
    {
        var unknownTypeId = Guid.NewGuid();

        var response = await _client.DeleteAsync($"/spark/po/{unknownTypeId}/people%2F1");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_returns_404_when_id_does_not_exist()
    {
        var response = await _client.DeleteAsync($"/spark/po/{PersonTypeId}/people%2Fdoes-not-exist");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_returns_204_and_removes_the_document()
    {
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new Person { FirstName = "Alice", LastName = "Smith" }, "people/1");
            await session.SaveChangesAsync();
        }

        var response = await _client.DeleteAsync($"/spark/po/{PersonTypeId}/people%2F1");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        WaitForIndexing(Store);
        using var verify = Store.OpenAsyncSession();
        var stored = await verify.LoadAsync<Person>("people/1");
        stored.Should().BeNull();
    }
}
