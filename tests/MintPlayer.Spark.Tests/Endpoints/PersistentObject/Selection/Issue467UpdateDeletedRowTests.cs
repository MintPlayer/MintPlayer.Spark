using System.Net;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.Exceptions;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using MintPlayer.Spark.Tests.Endpoints.PersistentObject;

using PO = MintPlayer.Spark.Abstractions.PersistentObject;

namespace MintPlayer.Spark.Tests.Endpoints.PersistentObject.Selection;

/// <summary>
/// #467 spike S11 — an Update of an object that was deleted after it was loaded (PRD D15), and an
/// Update that carries no etag at all (PRD D16).
/// </summary>
/// <remarks>
/// RED by design: these assert the behaviour the PRD decided, not what master does today. They turn
/// green with milestone M6.
/// </remarks>
public class Issue467UpdateDeletedRowTests : SparkTestDriver
{
    private static readonly Guid PersonTypeId = Guid.Parse("46700000-0000-4000-8000-000000000101");
    private const string Id = "people/467";

    private SparkEndpointFactory _factory = null!;
    private SparkClient _client = null!;
    private DeleteRace _race = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _race = new DeleteRace(Store);
        _factory = new SparkEndpointFactory(
            Store,
            [TestModels.Person(PersonTypeId)],
            configureServices: services => services.AddSingleton(_race),
            configureSpark: spark => spark.AddInterceptor<DeleteAfterPreReadInterceptor>());
        _client = new SparkClient(_factory.CreateClient(), ownsClient: true);
    }

    public override async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private async Task<PO> SeedAndLoadAsync()
    {
        await SeedAsync(session => session.StoreAsync(new Person { FirstName = "Alice", LastName = "Smith" }, Id));
        return await _client.GetPersistentObjectAsync(PersonTypeId, Id)
            ?? throw new InvalidOperationException($"Seeded document '{Id}' was not visible over HTTP.");
    }

    /// <summary>The other user's delete: a separate session, committed after this user loaded the row.</summary>
    private async Task DeleteConcurrentlyAsync()
    {
        using var session = Store.OpenAsyncSession();
        session.Delete(Id);
        await session.SaveChangesAsync();
    }

    private async Task<bool> ExistsAsync()
    {
        using var session = Store.OpenAsyncSession();
        return await session.LoadAsync<Person>(Id) is not null;
    }

    private static void SetAttribute(PO po, string name, object value)
        => (po.Attributes.FirstOrDefault(a => a.Name == name)
            ?? throw new InvalidOperationException($"Attribute '{name}' not on PO '{po.Id}'.")).Value = value;

    // S11 / D15 (A + C): the loaded etag is posted, the document is gone → 409, never a resurrection.
    // Observed today: NOT resurrected over HTTP — the Update endpoint's pre-read (Update.cs:47) answers
    // 404. Red only on the status: D15 wants 409 "deleted by another user".
    [Fact]
    public async Task S11_Update_of_a_row_deleted_after_load_with_the_loaded_etag_is_409_and_stays_deleted()
    {
        var po = await SeedAndLoadAsync();
        po.Etag.Should().NotBeNullOrEmpty();
        await DeleteConcurrentlyAsync();

        SetAttribute(po, "FirstName", "Alicia");
        var ex = await Record.ExceptionAsync(() => _client.UpdatePersistentObjectAsync(po));

        (await ExistsAsync()).Should().BeFalse("D15: saving an object deleted since it was loaded must not recreate it");
        ex.Should().BeOfType<SparkClientException>("D15: the save must be refused, not answered 200");
        ((SparkClientException)ex!).StatusCode.Should().Be(HttpStatusCode.Conflict, "D15: 'deleted by another user' is a 409");
    }

    // S11 / D15 + D16: the same, without an etag. D16 makes that a 400; either way, no resurrection.
    // Observed today: not resurrected, 404 (endpoint pre-read). Red only on the status.
    [Fact]
    public async Task S11_Update_of_a_row_deleted_after_load_without_etag_is_refused_and_stays_deleted()
    {
        var po = await SeedAndLoadAsync();
        await DeleteConcurrentlyAsync();

        po.Etag = null;
        SetAttribute(po, "FirstName", "Alicia");
        // The .NET client refuses to send it (D16), so the request goes out raw.
        var (status, body) = await RawSparkPost.UpdateAsync(_factory, po);

        (await ExistsAsync()).Should().BeFalse("D15: an etag-less save of a deleted object must not recreate it either");
        status.Should().BeOneOf([HttpStatusCode.BadRequest, HttpStatusCode.Conflict], $"D15/D16: the save must be refused; body: {body}");
    }

    // S11 / D15 through IDatabaseAccess directly — the chokepoint every internal caller uses.
    // Observed today: RESURRECTED. The side-session check skips a missing row (DatabaseAccess.cs:322-361),
    // and the base OnSaveAsync falls through to ToEntity + StoreAsync with no change vector.
    [Fact]
    public async Task S11_DatabaseAccess_save_of_a_row_deleted_after_load_throws_concurrency_and_stays_deleted()
    {
        var po = await SeedAndLoadAsync();
        await DeleteConcurrentlyAsync();
        SetAttribute(po, "FirstName", "Alicia");

        await using var scope = _factory.GetService<IServiceScopeFactory>().CreateAsyncScope();
        var databaseAccess = scope.ServiceProvider.GetRequiredService<IDatabaseAccess>();
        var ex = await Record.ExceptionAsync(() => databaseAccess.SavePersistentObjectAsync(po));

        (await ExistsAsync()).Should().BeFalse("D15: IDatabaseAccess must not recreate a deleted object on Save");
        ex.Should().BeOfType<SparkConcurrencyException>("D15: a save of a deleted object is the 409 exception");
    }

    // S11 / D15 (C): the delete lands AFTER the Update endpoint's pre-read (Update.cs:47) but before
    // the write. Made deterministic by an after-load interceptor that deletes the row through a
    // separate session the moment the endpoint's gated read returns it. Desired: 409, stays deleted.
    // CONTROL — GREEN today (observed 2026-10-03): the pre-read loads the row into the REQUEST session,
    // so the base OnSaveAsync's LoadAsync is an identity-map hit carrying the old change vector and the
    // write fails with Raven's ConcurrencyException → 409. Over HTTP the window is already closed;
    // the resurrection needs a caller that did not load the row in the same session (test above).
    [Fact]
    public async Task S11_control_Update_racing_a_delete_after_the_endpoint_pre_read_is_409_and_stays_deleted()
    {
        var po = await SeedAndLoadAsync();
        SetAttribute(po, "FirstName", "Alicia");

        _race.Armed = true;
        var ex = await Record.ExceptionAsync(() => _client.UpdatePersistentObjectAsync(po));

        _race.Fired.Should().BeTrue("the concurrent delete must have landed for this test to mean anything");
        (await ExistsAsync()).Should().BeFalse("D15: a delete landing between the pre-read and the write must not be undone");
        ex.Should().BeOfType<SparkClientException>("D15: the save must be refused, not answered 200");
        ((SparkClientException)ex!).StatusCode.Should().Be(HttpStatusCode.Conflict, "D15: 'deleted by another user' is a 409");
    }

    public sealed class DeleteRace(Raven.Client.Documents.IDocumentStore store)
    {
        public bool Armed { get; set; }
        public bool Fired { get; private set; }

        public async Task DeleteAsync(string id)
        {
            Armed = false;
            using var session = store.OpenAsyncSession();
            session.Delete(id);
            await session.SaveChangesAsync();
            Fired = true;
        }
    }

    public sealed class DeleteAfterPreReadInterceptor(DeleteRace race) : IAfterLoad
    {
        public bool AppliesTo(Type entityType) => entityType == typeof(Person);

        public async ValueTask OnAfterLoadAsync(LoadContext context)
        {
            if (race.Armed)
                await race.DeleteAsync(context.PersistentObject.Id!);
        }
    }

    // S11 / D16: an HTTP Update without an etag is a 400 (it is 200 today:
    // UpdateEndpointConcurrencyTests.Put_with_no_etag_skips_concurrency_check_and_succeeds pins that).
    [Fact]
    public async Task S11_Update_without_etag_is_400_and_writes_nothing()
    {
        var po = await SeedAndLoadAsync();

        po.Etag = null;
        SetAttribute(po, "FirstName", "Alicia");
        // The .NET client refuses to send it (D16), so the request goes out raw.
        var (status, body) = await RawSparkPost.UpdateAsync(_factory, po);

        using var session = Store.OpenAsyncSession();
        (await session.LoadAsync<Person>(Id)).FirstName.Should().Be("Alice", "D16: an etag-less update writes nothing");
        status.Should().Be(HttpStatusCode.BadRequest, $"D16: a missing etag is a 400; body: {body}");
    }
}
