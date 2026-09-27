using System.Net;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Endpoints.PersistentObject;

/// <summary>
/// <c>POST /spark/po/new</c> for a standalone object — construction, not persistence. Every existing
/// New test builds an <c>AsDetail</c> row, so the standalone branch had never run.
/// </summary>
public class StandaloneNewEndpointTests : SparkTestDriver
{
    private static readonly Guid PersonTypeId = Guid.Parse("55555555-ffff-ffff-ffff-555555555555");

    [Fact]
    public async Task New_scaffolds_an_unsaved_object_of_the_type()
    {
        await using var factory = new SparkEndpointFactory(Store, [TestModels.Person(PersonTypeId)]);
        using var client = new SparkClient(factory.CreateClient(), ownsClient: true);

        var po = await client.NewPersistentObjectAsync(PersonTypeId);

        po.ObjectTypeId.Should().Be(PersonTypeId);
        po.Name.Should().Be("Person");
        po.Id.Should().BeNullOrEmpty("nothing was saved");
        po.Attributes.Select(a => a.Name).Should().Contain("FirstName").And.Contain("LastName");

        using var session = Store.OpenAsyncSession();
        (await session.Query<Person>().CountAsync()).Should().Be(0, "New constructs; it never writes");
    }

    [Fact]
    public async Task New_by_alias_resolves_the_same_type()
    {
        await using var factory = new SparkEndpointFactory(Store, [TestModels.Person(PersonTypeId)]);
        using var client = new SparkClient(factory.CreateClient(), ownsClient: true);

        var po = await client.NewPersistentObjectAsync("person");

        po.ObjectTypeId.Should().Be(PersonTypeId);
    }

    [Fact]
    public async Task New_without_the_New_right_is_refused()
    {
        await using var factory = new SparkEndpointFactory(
            Store, [TestModels.Person(PersonTypeId)], security: SparkTestSecurity.Empty);
        using var client = new SparkClient(factory.CreateClient(), ownsClient: true);

        var ex = await Assert.ThrowsAsync<SparkClientException>(() => client.NewPersistentObjectAsync(PersonTypeId));

        ex.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task New_for_an_unknown_type_is_refused()
    {
        await using var factory = new SparkEndpointFactory(Store, [TestModels.Person(PersonTypeId)]);
        using var client = new SparkClient(factory.CreateClient(), ownsClient: true);

        var ex = await Assert.ThrowsAsync<SparkClientException>(() => client.NewPersistentObjectAsync(Guid.NewGuid()));

        ex.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
