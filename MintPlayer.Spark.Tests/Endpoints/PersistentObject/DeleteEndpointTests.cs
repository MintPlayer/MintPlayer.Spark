using System.Net;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Endpoints.PersistentObject;

public class DeleteEndpointTests : SparkTestDriver
{
    private static readonly Guid PersonTypeId = Guid.Parse("66666666-eeee-eeee-eeee-666666666666");

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
    public async Task Delete_throws_404_when_entity_type_is_unknown()
    {
        var ex = await Assert.ThrowsAsync<SparkClientException>(
            () => _client.DeletePersistentObjectAsync(Guid.NewGuid(), "people/1"));

        ex.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_throws_404_when_id_does_not_exist()
    {
        var ex = await Assert.ThrowsAsync<SparkClientException>(
            () => _client.DeletePersistentObjectAsync(PersonTypeId, "people/does-not-exist"));

        ex.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_removes_the_document()
    {
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new Person { FirstName = "Alice", LastName = "Smith" }, "people/1");
            await session.SaveChangesAsync();
        }

        await _client.DeletePersistentObjectAsync(PersonTypeId, "people/1");

        WaitForIndexing(Store);
        using var verify = Store.OpenAsyncSession();
        var stored = await verify.LoadAsync<Person>("people/1");
        stored.Should().BeNull();
    }
}
