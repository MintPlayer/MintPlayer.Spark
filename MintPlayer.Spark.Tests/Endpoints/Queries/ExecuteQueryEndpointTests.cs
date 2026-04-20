using System.Net;
using System.Text.Json;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents;
using TestModels = MintPlayer.Spark.Tests.Endpoints.PersistentObject.TestModels;

namespace MintPlayer.Spark.Tests.Endpoints.Queries;

public class ExecuteQueryEndpointTests : SparkTestDriver
{
    private static readonly Guid PersonTypeId = Guid.Parse("aaaa3333-3333-3333-3333-aaaaaaaaaaaa");
    private static readonly Guid AllPeopleQueryId = Guid.Parse("bbbb3333-3333-3333-3333-bbbbbbbbbbbb");

    private SparkEndpointFactory _factory = null!;
    private HttpClient _client = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        var personType = TestModels.Person(PersonTypeId);
        personType.Queries =
        [
            new SparkQuery
            {
                Id = AllPeopleQueryId,
                Name = "AllPeople",
                Source = "Database.People",
            },
        ];

        _factory = new SparkEndpointFactory(Store, [personType]);
        _client = _factory.CreateClient();
    }

    public override async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private async Task SeedAsync(params (string id, string first, string last)[] people)
    {
        using var session = Store.OpenAsyncSession();
        foreach (var (id, first, last) in people)
            await session.StoreAsync(new Person { FirstName = first, LastName = last }, id);
        await session.SaveChangesAsync();
        WaitForIndexing(Store);
    }

    [Fact]
    public async Task Execute_returns_404_when_query_unknown()
    {
        var response = await _client.GetAsync($"/spark/queries/{Guid.NewGuid()}/execute");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Execute_returns_seeded_results()
    {
        await SeedAsync(
            ("people/1", "Alice", "Smith"),
            ("people/2", "Bob", "Jones"));

        var response = await _client.GetAsync($"/spark/queries/{AllPeopleQueryId}/execute");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        var parsed = JsonDocument.Parse(body);
        parsed.RootElement.GetProperty("totalRecords").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Execute_returns_empty_envelope_with_no_data()
    {
        var response = await _client.GetAsync($"/spark/queries/{AllPeopleQueryId}/execute");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        var parsed = JsonDocument.Parse(body);
        parsed.RootElement.GetProperty("totalRecords").GetInt32().Should().Be(0);
        parsed.RootElement.GetProperty("data").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Execute_applies_search_filter_case_insensitively()
    {
        await SeedAsync(
            ("people/1", "Alice", "Smith"),
            ("people/2", "Bob", "Jones"),
            ("people/3", "Carol", "Davis"));

        var response = await _client.GetAsync($"/spark/queries/{AllPeopleQueryId}/execute?search=ALICE");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        var parsed = JsonDocument.Parse(body);
        parsed.RootElement.GetProperty("totalRecords").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Execute_honors_skip_and_take_query_parameters()
    {
        await SeedAsync(
            Enumerable.Range(1, 10)
                .Select(i => ($"people/{i}", $"First{i}", $"Last{i}"))
                .ToArray());

        var response = await _client.GetAsync($"/spark/queries/{AllPeopleQueryId}/execute?skip=3&take=4");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        var parsed = JsonDocument.Parse(body);
        parsed.RootElement.GetProperty("totalRecords").GetInt32().Should().Be(10);
        parsed.RootElement.GetProperty("skip").GetInt32().Should().Be(3);
        parsed.RootElement.GetProperty("take").GetInt32().Should().Be(4);
        parsed.RootElement.GetProperty("data").GetArrayLength().Should().Be(4);
    }

    [Fact]
    public async Task Execute_falls_back_to_default_pagination_when_params_missing()
    {
        await SeedAsync(("people/1", "A", "B"));

        var response = await _client.GetAsync($"/spark/queries/{AllPeopleQueryId}/execute");

        var parsed = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        parsed.RootElement.GetProperty("skip").GetInt32().Should().Be(0);
        parsed.RootElement.GetProperty("take").GetInt32().Should().Be(50);
    }

    [Fact]
    public async Task Execute_resolves_query_by_alias()
    {
        await SeedAsync(("people/1", "Alice", "Smith"));

        var response = await _client.GetAsync("/spark/queries/allpeople/execute");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
