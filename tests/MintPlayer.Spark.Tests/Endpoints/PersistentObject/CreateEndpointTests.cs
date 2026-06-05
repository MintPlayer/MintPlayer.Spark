using System.Net;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using PO = MintPlayer.Spark.Abstractions.PersistentObject;
using POA = MintPlayer.Spark.Abstractions.PersistentObjectAttribute;

namespace MintPlayer.Spark.Tests.Endpoints.PersistentObject;

public class CreateEndpointTests : SparkTestDriver
{
    private static readonly Guid PersonTypeId = Guid.Parse("33333333-cccc-cccc-cccc-333333333333");

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

    private static PO NewPerson(Guid typeId, string firstName, string lastName) => new()
    {
        Name = "Person",
        ObjectTypeId = typeId,
        Attributes =
        [
            new POA { Name = "FirstName", Value = firstName },
            new POA { Name = "LastName", Value = lastName },
        ],
    };

    [Fact]
    public async Task Create_throws_404_when_entity_type_is_unknown()
    {
        // Create endpoint routes on PO.Name as the URL segment — override both Name and
        // ObjectTypeId so neither resolves server-side, proving the 404 path.
        var unknownName = $"unknown-type-{Guid.NewGuid():N}";
        var ex = await Assert.ThrowsAsync<SparkClientException>(
            () => _client.CreatePersistentObjectAsync(new PO
            {
                Name = unknownName,
                ObjectTypeId = Guid.NewGuid(),
                Attributes = [],
            }));

        ex.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Create_persists_a_new_document()
    {
        var created = await _client.CreatePersistentObjectAsync(NewPerson(PersonTypeId, "Alice", "Smith"));
        created.Id.Should().NotBeNullOrEmpty();

        WaitForIndexing(Store);
        using var session = Store.OpenAsyncSession();
        var stored = await session.Query<Person>().ToListAsync();
        stored.Should().ContainSingle().Which.FirstName.Should().Be("Alice");
    }

    [Fact]
    public async Task Create_resolves_entity_type_by_alias()
    {
        // Build the PO with Name="person" (lowercase) — the endpoint route uses {name} as alias.
        var created = await _client.CreatePersistentObjectAsync(new PO
        {
            Name = "person",  // alias
            ObjectTypeId = PersonTypeId,
            Attributes =
            [
                new POA { Name = "FirstName", Value = "Bob" },
                new POA { Name = "LastName", Value = "Jones" },
            ],
        });

        created.Id.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Create_throws_400_when_request_body_fails_validation()
    {
        // Required LastName missing — validation rejects before persisting.
        var typeWithRequired = TestModels.PersonWithRequiredLastName(Guid.Parse("44444444-cccc-cccc-cccc-444444444444"));
        await using var factory = new SparkEndpointFactory(Store, [typeWithRequired]);
        using var client = new SparkClient(factory.CreateClient(), ownsClient: true);

        var ex = await Assert.ThrowsAsync<SparkClientException>(
            () => client.CreatePersistentObjectAsync(new PO
            {
                Name = "Person",
                ObjectTypeId = typeWithRequired.PersistentObject.Id,
                Attributes =
                [
                    new POA { Name = "FirstName", Value = "Alice" },
                    // LastName intentionally omitted
                ],
            }));

        ex.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
