using System.Net;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Endpoints.PersistentObject;

/// <summary>One permissive host for the class (M8 item 5): nothing here writes a document.</summary>
public sealed class StandaloneNewEndpointHost : SharedSparkHost<TestSparkContext>
{
    internal static readonly Guid PersonTypeId = Guid.Parse("55555555-ffff-ffff-ffff-555555555555");

    protected override SparkEndpointFactory<TestSparkContext> CreateFactory()
        => new SparkEndpointFactory(Store, [TestModels.Person(PersonTypeId)]);
}

/// <summary>
/// <c>POST /spark/po/new</c> for a standalone object — construction, not persistence. Every existing
/// New test builds an <c>AsDetail</c> row, so the standalone branch had never run.
/// </summary>
public class StandaloneNewEndpointTests(StandaloneNewEndpointHost host)
    : SparkSharedTestDriver(host), IClassFixture<StandaloneNewEndpointHost>
{
    private static readonly Guid PersonTypeId = StandaloneNewEndpointHost.PersonTypeId;

    [Fact]
    public async Task New_scaffolds_an_unsaved_object_of_the_type()
    {
        var factory = host.Factory;
        using var client = new SparkClient(factory.CreateClient(), ownsClient: true);

        var po = await client.NewPersistentObjectAsync(PersonTypeId);

        po.ObjectTypeId.Should().Be(PersonTypeId);
        po.Name.Should().Be("Person");
        po.Id.Should().BeNullOrEmpty("nothing was saved");
        po.Attributes.Select(a => a.Name).Should().Contain("FirstName").And.Contain("LastName");

        // Unscoped on purpose and still sound on the shared database: no case in this class writes
        // a Person, so any Person at all is one a New call wrote.
        using var session = Store.OpenAsyncSession();
        (await session.Query<Person>().CountAsync()).Should().Be(0, "New constructs; it never writes");
    }

    [Fact]
    public async Task New_by_alias_resolves_the_same_type()
    {
        var factory = host.Factory;
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

        var ex = (await new Func<Task>(() => client.NewPersistentObjectAsync(PersonTypeId)).Should().ThrowExactlyAsync<SparkClientException>()).Which;

        ex.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task New_for_an_unknown_type_is_refused()
    {
        var factory = host.Factory;
        using var client = new SparkClient(factory.CreateClient(), ownsClient: true);

        var ex = (await new Func<Task>(() => client.NewPersistentObjectAsync(Guid.NewGuid())).Should().ThrowExactlyAsync<SparkClientException>()).Which;

        ex.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
