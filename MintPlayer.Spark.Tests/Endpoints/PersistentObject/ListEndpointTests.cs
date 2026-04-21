using System.Net;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Endpoints.PersistentObject;

public class ListEndpointTests : SparkTestDriver
{
    private static readonly Guid PersonTypeId = Guid.Parse("22222222-bbbb-bbbb-bbbb-222222222222");

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
    public async Task List_throws_404_when_entity_type_is_unknown()
    {
        var ex = await Assert.ThrowsAsync<SparkClientException>(
            () => _client.ListPersistentObjectsAsync(Guid.NewGuid()));

        ex.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task List_returns_empty_array_when_no_documents_exist()
    {
        var result = await _client.ListPersistentObjectsAsync(PersonTypeId);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task List_returns_all_seeded_documents()
    {
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new Person { FirstName = "Alice", LastName = "Smith" }, "people/1");
            await session.StoreAsync(new Person { FirstName = "Bob", LastName = "Jones" }, "people/2");
            await session.StoreAsync(new Person { FirstName = "Carol", LastName = "Davis" }, "people/3");
            await session.SaveChangesAsync();
        }
        WaitForIndexing(Store);

        var result = await _client.ListPersistentObjectsAsync(PersonTypeId);

        result.Should().HaveCount(3);
        result.Select(p => p.Id).Should().BeEquivalentTo(new[] { "people/1", "people/2", "people/3" });
    }

    [Fact]
    public async Task List_resolves_entity_type_by_alias()
    {
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new Person { FirstName = "Alice", LastName = "Smith" }, "people/1");
            await session.SaveChangesAsync();
        }
        WaitForIndexing(Store);

        var result = await _client.ListPersistentObjectsAsync("person");

        result.Should().ContainSingle()
            .Which.Attributes.Should().Contain(a => a.Name == "FirstName" && a.Value!.ToString() == "Alice");
    }
}
