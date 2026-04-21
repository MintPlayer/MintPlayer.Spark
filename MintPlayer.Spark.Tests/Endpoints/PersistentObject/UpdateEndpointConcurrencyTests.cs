using System.Net;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents;

// Using the Abstractions.PersistentObject type by its full namespace avoids collision with
// the current namespace's own name (this file lives under MintPlayer.Spark.Tests.Endpoints.PersistentObject).
using PO = MintPlayer.Spark.Abstractions.PersistentObject;

namespace MintPlayer.Spark.Tests.Endpoints.PersistentObject;

/// <summary>
/// M-7 — optimistic concurrency on <c>PUT /spark/po</c>. The unit-test counterpart of
/// <c>MintPlayer.Spark.E2E.Tests/Security/ConcurrencyTests.cs</c>: covers the same
/// <c>SparkConcurrencyException</c> throw inside <c>DatabaseAccess</c> and the 409 catch block
/// in the <c>UpdatePersistentObject</c> endpoint, but in-process so it runs on CI as part of
/// MintPlayer.Spark.Tests and contributes to coverage. Uses <see cref="SparkClient"/>, so the
/// test body reads as domain operations (get the PO, update it) rather than hand-built JSON.
/// </summary>
public class UpdateEndpointConcurrencyTests : SparkTestDriver
{
    private static readonly Guid PersonTypeId = Guid.Parse("c0cc1111-eeee-eeee-eeee-c0cc1111eeee");

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

    private async Task<PO> SeedAndLoadAsync(string id, string firstName, string lastName)
    {
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new Person { FirstName = firstName, LastName = lastName }, id);
            await session.SaveChangesAsync();
        }
        await RavenIndexHelper.WaitForNonStaleAsync(Store);

        return await _client.GetPersistentObjectAsync(PersonTypeId, id)
            ?? throw new InvalidOperationException($"Seeded document '{id}' was not visible over HTTP.");
    }

    private static void SetAttribute(PO po, string name, object value)
    {
        var attr = po.Attributes.FirstOrDefault(a => a.Name == name);
        if (attr is null) throw new InvalidOperationException($"Attribute '{name}' not on PO '{po.Id}'.");
        attr.Value = value;
    }

    [Fact]
    public async Task Put_with_stale_etag_throws_409_conflict()
    {
        var poA = await SeedAndLoadAsync("people/1", "Alice", "Smith");
        var etagV1 = poA.Etag;

        // Client A advances the server from v1 → v2.
        SetAttribute(poA, "FirstName", "Alicia");
        await _client.UpdatePersistentObjectAsync(poA);

        // Client B still holds the v1 snapshot — its etag no longer matches the server.
        var poB = await _client.GetPersistentObjectAsync(PersonTypeId, "people/1");
        poB!.Etag = etagV1;
        SetAttribute(poB, "LastName", "Jones");

        var ex = await Assert.ThrowsAsync<SparkClientException>(
            () => _client.UpdatePersistentObjectAsync(poB));
        ex.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Put_with_matching_etag_succeeds_and_returns_new_etag()
    {
        var po = await SeedAndLoadAsync("people/1", "Alice", "Smith");
        var etagV1 = po.Etag;

        SetAttribute(po, "LastName", "Smith-Jones");
        var saved = await _client.UpdatePersistentObjectAsync(po);

        saved.Etag.Should().NotBeNullOrEmpty();
        saved.Etag.Should().NotBe(etagV1, "server must advance the change vector after a write");
    }

    [Fact]
    public async Task Put_with_no_etag_skips_concurrency_check_and_succeeds()
    {
        var po = await SeedAndLoadAsync("people/1", "Alice", "Smith");

        // Clients that don't round-trip the change vector (legacy path) must still work.
        po.Etag = null;
        SetAttribute(po, "FirstName", "Alicia");
        var saved = await _client.UpdatePersistentObjectAsync(po);

        saved.Should().NotBeNull();
    }
}
