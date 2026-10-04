using System.Net;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents;
using PO = MintPlayer.Spark.Abstractions.PersistentObject;

namespace MintPlayer.Spark.Tests.Endpoints.PersistentObject;

public class UpdateEndpointTests : SparkTestDriver
{
    private static readonly Guid PersonTypeId = Guid.Parse("55555555-dddd-dddd-dddd-555555555555");

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

    private static PO NewPerson(string id, string firstName, string lastName) => new()
    {
        Id = id,
        Name = "Person",
        ObjectTypeId = PersonTypeId,
        Attributes =
        [
            new() { Name = "FirstName", Value = firstName },
            new() { Name = "LastName", Value = lastName },
        ],
    };

    [Fact]
    public async Task Update_throws_404_when_entity_type_is_unknown()
    {
        var po = NewPerson("people/1", "A", "B");
        po.ObjectTypeId = Guid.NewGuid();  // force unknown type
        po.Etag = StoredEtag.ForMissingRow;

        var ex = await Assert.ThrowsAsync<SparkClientException>(() => _client.UpdatePersistentObjectAsync(po));

        ex.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The caller holds an etag, so it loaded the row once: a row that is gone is "deleted since you
    /// loaded it", 409 <c>deleted</c> (#467, D30c), never a silent re-create.
    /// </summary>
    [Fact]
    public async Task Update_of_an_id_that_does_not_exist_is_409_deleted()
    {
        var po = NewPerson("people/does-not-exist", "A", "B");
        po.Etag = StoredEtag.ForMissingRow;

        var ex = await Assert.ThrowsAsync<SparkClientException>(() => _client.UpdatePersistentObjectAsync(po));

        ex.StatusCode.Should().Be(HttpStatusCode.Conflict);
        ex.ResponseBody.Should().Contain("deleted");
    }

    [Fact]
    public async Task Update_modifies_existing_document()
    {
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new Person { FirstName = "Alice", LastName = "Smith" }, "people/1");
            await session.SaveChangesAsync();
        }

        var edit = NewPerson("people/1", "Alicia", "Smith-Jones");
        edit.Etag = await StoredEtag.OfAsync(Store, "people/1");
        var saved = await _client.UpdatePersistentObjectAsync(edit);
        saved.Should().NotBeNull();

        await Store.WaitForIndexingAsync();
        using var verify = Store.OpenAsyncSession();
        var stored = await verify.LoadAsync<Person>("people/1");
        stored.FirstName.Should().Be("Alicia");
        stored.LastName.Should().Be("Smith-Jones");
    }
}
