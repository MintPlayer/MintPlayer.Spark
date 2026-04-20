using System.Net;
using System.Net.Http.Json;
using MintPlayer.Spark.Tests._Infrastructure;

namespace MintPlayer.Spark.Tests.Endpoints.PersistentObject;

/// <summary>
/// Verifies that Spark's antiforgery middleware rejects mutating requests without
/// an X-XSRF-TOKEN header. See PRD-Testing.md §13.1.
/// </summary>
public class AntiforgerySecurityTests : MintPlayer.Spark.Testing.SparkTestDriver
{
    private static readonly Guid PersonTypeId = Guid.Parse("55555555-dddd-dddd-dddd-555555555555");

    private SparkEndpointFactory _factory = null!;
    private HttpClient _bareClient = null!; // no antiforgery cookie/header

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _factory = new SparkEndpointFactory(Store, [TestModels.Person(PersonTypeId)]);
        _bareClient = _factory.CreateClient();
    }

    public override async Task DisposeAsync()
    {
        _bareClient.Dispose();
        await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task POST_without_antiforgery_token_is_rejected_with_400()
    {
        var response = await _bareClient.PostAsJsonAsync(
            $"/spark/po/{PersonTypeId}",
            new
            {
                persistentObject = new
                {
                    name = "Person",
                    objectTypeId = PersonTypeId,
                    attributes = new[]
                    {
                        new { name = "FirstName", value = (object)"Alice" },
                        new { name = "LastName", value = (object)"Smith" },
                    }
                }
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
