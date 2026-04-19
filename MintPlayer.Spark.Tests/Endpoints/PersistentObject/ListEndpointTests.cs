using System.Net;
using System.Text.Json;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;

namespace MintPlayer.Spark.Tests.Endpoints.PersistentObject;

public class ListEndpointTests : SparkTestDriver
{
    private static readonly Guid PersonTypeId = Guid.Parse("22222222-bbbb-bbbb-bbbb-222222222222");

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
    public async Task List_returns_404_when_entity_type_is_unknown()
    {
        var unknownTypeId = Guid.NewGuid();

        var response = await _client.GetAsync($"/spark/po/{unknownTypeId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task List_returns_empty_array_when_no_documents_exist()
    {
        var response = await _client.GetAsync($"/spark/po/{PersonTypeId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        var parsed = JsonDocument.Parse(body);
        parsed.RootElement.GetArrayLength().Should().Be(0);
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

        var response = await _client.GetAsync($"/spark/po/{PersonTypeId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        var parsed = JsonDocument.Parse(body);
        parsed.RootElement.GetArrayLength().Should().Be(3);
        body.Should().Contain("\"id\":\"people/1\"");
        body.Should().Contain("\"id\":\"people/2\"");
        body.Should().Contain("\"id\":\"people/3\"");
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

        var response = await _client.GetAsync("/spark/po/person");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Alice");
    }
}
