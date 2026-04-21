using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using TestModels = MintPlayer.Spark.Tests.Endpoints.PersistentObject.TestModels;

namespace MintPlayer.Spark.Tests.Endpoints.Queries;

public class ListQueriesEndpointTests : SparkTestDriver
{
    private static readonly Guid PersonTypeId = Guid.Parse("aaaa2222-2222-2222-2222-aaaaaaaaaaaa");
    private static readonly Guid AllPeopleQueryId = Guid.Parse("bbbb2222-2222-2222-2222-bbbbbbbbbbbb");
    private static readonly Guid OnlyAdminsQueryId = Guid.Parse("cccc2222-2222-2222-2222-cccccccccccc");

    [Fact]
    public async Task List_returns_empty_when_no_queries_defined()
    {
        var personType = TestModels.Person(Guid.NewGuid()); // no Queries
        await using var factory = new SparkEndpointFactory(Store, [personType]);
        using var client = new SparkClient(factory.CreateClient(), ownsClient: true);

        var queries = await client.ListQueriesAsync();

        queries.Should().BeEmpty();
    }

    [Fact]
    public async Task List_returns_all_queries_across_all_entity_types()
    {
        var personType = TestModels.Person(PersonTypeId);
        personType.Queries =
        [
            new SparkQuery { Id = AllPeopleQueryId, Name = "AllPeople", Source = "Database.People" },
            new SparkQuery { Id = OnlyAdminsQueryId, Name = "OnlyAdmins", Source = "Custom.GetAdmins", EntityType = "Person" },
        ];

        await using var factory = new SparkEndpointFactory(Store, [personType]);
        using var client = new SparkClient(factory.CreateClient(), ownsClient: true);

        var queries = await client.ListQueriesAsync();

        queries.Select(q => q.Name).Should().BeEquivalentTo(new[] { "AllPeople", "OnlyAdmins" });
    }
}
