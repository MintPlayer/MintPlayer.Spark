using System.Net;
using System.Text.Json;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using TestModels = MintPlayer.Spark.Tests.Endpoints.PersistentObject.TestModels;

namespace MintPlayer.Spark.Tests.Endpoints.Queries;

public class GetQueryEndpointTests : SparkTestDriver
{
    private static readonly Guid PersonTypeId = Guid.Parse("aaaa1111-1111-1111-1111-aaaaaaaaaaaa");
    private static readonly Guid AllPeopleQueryId = Guid.Parse("bbbb1111-1111-1111-1111-bbbbbbbbbbbb");

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

    [Fact]
    public async Task Get_returns_404_when_query_id_unknown()
    {
        var response = await _client.GetAsync($"/spark/queries/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_returns_query_definition_by_GUID()
    {
        var response = await _client.GetAsync($"/spark/queries/{AllPeopleQueryId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"name\":\"AllPeople\"");
        body.Should().Contain("\"source\":\"Database.People\"");
    }

    [Fact]
    public async Task Get_resolves_query_by_alias()
    {
        // Auto-generated alias: "GetX" → "x"; for "AllPeople" the alias is "allpeople"
        var response = await _client.GetAsync("/spark/queries/allpeople");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"name\":\"AllPeople\"");
    }
}
