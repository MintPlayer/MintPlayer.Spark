using System.Net;
using System.Net.Http.Json;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;

namespace MintPlayer.Spark.Tests.Endpoints.PersistentObject;

/// <summary>
/// A create, update or refresh body that names a real type but carries no <c>persistentObject</c>.
/// Each endpoint used to throw <c>InvalidOperationException("PersistentObject is required.")</c>, which
/// answered 500. A malformed body is refused exactly like an unknown type instead — for refresh that
/// matters beyond tidiness, because the throw came before the permission check, so a 500 set against
/// a refusal told an unauthorized caller the type exists.
/// </summary>
public class MissingPersistentObjectBodyTests : SparkTestDriver
{
    private static readonly Guid PersonTypeId = Guid.Parse("55555555-eeee-eeee-eeee-555555555555");

    private SparkEndpointFactory _factory = null!;
    private SparkClient _client = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _factory = new SparkEndpointFactory(Store, [TestModels.Person(PersonTypeId)]);
        _client = new SparkClient(_factory.CreateClient(), ownsClient: true);

        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(new Person { FirstName = "Alice", LastName = "Smith" }, "people/1");
        await session.SaveChangesAsync();
    }

    public override async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private async Task<(HttpStatusCode Status, string Body)> PostAsync(string url, object body)
    {
        using var response = await _client.SendAsync(HttpMethod.Post, url, JsonContent.Create(body), requiresAntiforgery: true);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/spark/po/create")]
    [InlineData("/spark/po/update")]
    [InlineData("/spark/po/refresh")]
    public async Task A_body_without_the_object_is_refused_like_an_unknown_type(string url)
    {
        var missing = await PostAsync(url, new { objectTypeId = PersonTypeId.ToString(), id = "people/1" });
        var unknown = await PostAsync(url, new { objectTypeId = Guid.NewGuid().ToString(), id = "people/1" });

        missing.Status.Should().NotBe(HttpStatusCode.InternalServerError);
        missing.Status.Should().Be(unknown.Status);
        missing.Body.Should().Be(unknown.Body, "the refusal must not distinguish a real type from a missing one");
    }
}
