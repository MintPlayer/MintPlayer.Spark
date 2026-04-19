using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Endpoints.PersistentObject;

public class CreateEndpointTests : SparkTestDriver
{
    private static readonly Guid PersonTypeId = Guid.Parse("33333333-cccc-cccc-cccc-333333333333");

    private SparkEndpointFactory _factory = null!;
    private SparkTestClient _client = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _factory = new SparkEndpointFactory(Store, [TestModels.Person(PersonTypeId)]);
        _client = await _factory.CreateAuthorizedClientAsync();
    }

    public override async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private static object NewPersonRequest(string firstName, string lastName) => new
    {
        persistentObject = new
        {
            name = "Person",
            objectTypeId = PersonTypeId,
            attributes = new[]
            {
                new { name = "FirstName", value = (object)firstName },
                new { name = "LastName", value = (object)lastName },
            }
        }
    };

    [Fact]
    public async Task Create_returns_404_when_entity_type_is_unknown()
    {
        var unknownTypeId = Guid.NewGuid();

        var response = await _client.PostJsonAsync($"/spark/po/{unknownTypeId}", NewPersonRequest("Alice", "Smith"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Create_returns_201_and_persists_a_new_document()
    {
        var response = await _client.PostJsonAsync($"/spark/po/{PersonTypeId}", NewPersonRequest("Alice", "Smith"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Alice");
        body.Should().Contain("Smith");

        // Verify the document made it to RavenDB
        WaitForIndexing(Store);
        using var session = Store.OpenAsyncSession();
        var stored = await session.Query<Person>().ToListAsync();
        stored.Should().ContainSingle()
            .Which.FirstName.Should().Be("Alice");
    }

    [Fact]
    public async Task Create_resolves_entity_type_by_alias()
    {
        var response = await _client.PostJsonAsync("/spark/po/person", NewPersonRequest("Bob", "Jones"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Create_returns_400_when_request_body_fails_validation()
    {
        // Required field missing — validation pipeline rejects before persisting
        var typeWithRequired = TestModels.PersonWithRequiredLastName(Guid.Parse("44444444-cccc-cccc-cccc-444444444444"));
        await using var factory = new SparkEndpointFactory(Store, [typeWithRequired]);
        using var client = await factory.CreateAuthorizedClientAsync();

        var response = await client.PostJsonAsync(
            $"/spark/po/{typeWithRequired.PersistentObject.Id}",
            new
            {
                persistentObject = new
                {
                    name = "Person",
                    objectTypeId = typeWithRequired.PersistentObject.Id,
                    attributes = new[]
                    {
                        new { name = "FirstName", value = (object)"Alice" },
                        // LastName intentionally missing
                    },
                },
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
