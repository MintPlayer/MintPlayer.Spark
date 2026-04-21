using System.Net;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using MintPlayer.Spark.Tests.Endpoints.PersistentObject;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Client;

/// <summary>
/// Covers the Phase 1 additions that don't require auth wiring: the string-alias overload of
/// <see cref="SparkClient.GetPersistentObjectAsync(string, string, System.Threading.CancellationToken)"/>
/// and the 404-on-unknown-entity-type path through <see cref="SparkClient.ExecuteActionAsync"/>.
/// Full coverage of login/logout/register + action happy path + retry-action live in the
/// dedicated <c>MintPlayer.Spark.Client.Tests</c> project (Phase 3) where the harness also
/// boots Spark.Authorization.
/// </summary>
public class SparkClientAliasAndActionTests : SparkTestDriver
{
    private static readonly Guid PersonTypeId = Guid.Parse("7aaaa111-0000-0000-0000-7aaaa1110000");

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
    public async Task Alias_overload_returns_same_result_as_guid_overload()
    {
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new Person { FirstName = "Alice", LastName = "Smith" }, "people/1");
            await session.SaveChangesAsync();
        }
        await RavenIndexHelper.WaitForNonStaleAsync(Store);

        var byGuid = await _client.GetPersistentObjectAsync(PersonTypeId, "people/1");
        var byAlias = await _client.GetPersistentObjectAsync("person", "people/1");

        byGuid.Should().NotBeNull();
        byAlias.Should().NotBeNull();
        byAlias!.Id.Should().Be(byGuid!.Id);
        byAlias.ObjectTypeId.Should().Be(byGuid.ObjectTypeId);
    }

    [Fact]
    public async Task Alias_overload_returns_null_when_id_not_found()
    {
        var result = await _client.GetPersistentObjectAsync("person", "people/missing");

        result.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteActionAsync_throws_not_found_when_entity_type_is_unknown()
    {
        var ex = await Assert.ThrowsAsync<SparkClientException>(
            () => _client.ExecuteActionAsync(Guid.NewGuid(), "Archive"));

        ex.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
