using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using TestModels = MintPlayer.Spark.Tests.Endpoints.PersistentObject.TestModels;

namespace MintPlayer.Spark.Tests.Endpoints.Queries;

public class GetQueryEndpointTests : SparkTestDriver
{
    private static readonly Guid PersonTypeId = Guid.Parse("aaaa1111-1111-1111-1111-aaaaaaaaaaaa");
    private static readonly Guid AllPeopleQueryId = Guid.Parse("bbbb1111-1111-1111-1111-bbbbbbbbbbbb");

    private SparkEndpointFactory _factory = null!;
    private SparkClient _client = null!;

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
        _client = new SparkClient(_factory.CreateClient(), ownsClient: true);
    }

    public override async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task Get_returns_null_when_query_id_unknown()
    {
        var result = await _client.GetQueryAsync(Guid.NewGuid());

        result.Should().BeNull();
    }

    [Fact]
    public async Task Get_returns_query_definition_by_GUID()
    {
        var query = await _client.GetQueryAsync(AllPeopleQueryId);

        query.Should().NotBeNull();
        query!.Name.Should().Be("AllPeople");
        query.Source.Should().Be("Database.People");
    }

    [Fact]
    public async Task Get_resolves_query_by_alias()
    {
        // Auto-generated alias: "GetX" → "x"; for "AllPeople" the alias is "allpeople"
        var query = await _client.GetQueryAsync("allpeople");

        query.Should().NotBeNull();
        query!.Name.Should().Be("AllPeople");
    }
}
