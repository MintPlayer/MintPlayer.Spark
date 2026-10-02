using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using TestModels = MintPlayer.Spark.Tests.Endpoints.PersistentObject.TestModels;

namespace MintPlayer.Spark.Tests.Endpoints.Queries;

/// <summary>One host for the class: every case only reads query definitions (M8 item 5).</summary>
public sealed class GetQueryEndpointHost : SharedSparkHost<TestSparkContext>
{
    internal static readonly Guid PersonTypeId = Guid.Parse("aaaa1111-1111-1111-1111-aaaaaaaaaaaa");
    internal static readonly Guid AllPeopleQueryId = Guid.Parse("bbbb1111-1111-1111-1111-bbbbbbbbbbbb");

    protected override SparkEndpointFactory<TestSparkContext> CreateFactory()
    {
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

        return new SparkEndpointFactory(Store, [personType]);
    }
}

public class GetQueryEndpointTests(GetQueryEndpointHost host)
    : SparkSharedTestDriver(host), IClassFixture<GetQueryEndpointHost>, IDisposable
{
    private static readonly Guid AllPeopleQueryId = GetQueryEndpointHost.AllPeopleQueryId;

    private readonly SparkClient _client = new(host.Factory.CreateClient(), ownsClient: true);

    public void Dispose() => _client.Dispose();

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
