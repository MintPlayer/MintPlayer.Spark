using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents;

using PO = MintPlayer.Spark.Abstractions.PersistentObject;

namespace MintPlayer.Spark.Tests.Endpoints.PersistentObject;

/// <summary>
/// Contributions F7 (M1b) — a concurrent writer landing <b>between</b> the framework's checks and its
/// write. <see cref="UpdateEndpointConcurrencyTests"/> covers a stale etag the check sees; this covers
/// the window the check cannot see.
/// </summary>
/// <remarks>
/// <para>
/// The race is made deterministic by an interceptor: its before-hook runs after the etag check and
/// after the base <c>OnSaveAsync</c> loaded the row, and before the write. Once per armed test it
/// writes the same document through a SEPARATE session — the other user's save, landing mid-request.
/// </para>
/// <para>
/// What these caught (all red before the fix): the save wrote without a change vector, so it
/// returned 200 and silently overwrote the concurrent writer's LastName with the stale one it had
/// loaded — with or without a client etag (the check compared in a side session, then the write
/// trusted nothing). A soft (replaced) delete had the same lost update. The fix writes with the
/// expected change vector and maps RavenDB's <c>ConcurrencyException</c> to the 409 envelope.
/// </para>
/// </remarks>
public class ConcurrentWriteRaceTests : SparkTestDriver
{
    private static readonly Guid PersonTypeId = Guid.Parse("c0cc2222-eeee-eeee-eeee-c0cc2222eeee");
    private const string Id = "people/1";

    // A RavenDB change vector: "A:12-7Hk2F9s3kUeQ3r0cXnQa1w", possibly several comma-separated.
    private static readonly Regex ChangeVectorPattern = new(@"[A-Z]{1,4}:\d+-[A-Za-z0-9+/_\-]{8,}", RegexOptions.Compiled);

    private SparkEndpointFactory _factory = null!;
    private SparkClient _client = null!;
    private RaceSwitch _race = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _race = new RaceSwitch(Store);
        _factory = new SparkEndpointFactory(
            Store,
            [TestModels.Person(PersonTypeId)],
            configureServices: services => services.AddSingleton(_race),
            configureSpark: spark => spark.AddPersistentObjectInterceptor<ConcurrentWriterInterceptor>());
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

    private static void SetAttribute(PO po, string name, object value)
    {
        var attr = po.Attributes.FirstOrDefault(a => a.Name == name)
            ?? throw new InvalidOperationException($"Attribute '{name}' not on PO '{po.Id}'.");
        attr.Value = value;
    }

    private async Task<(Person Stored, string ChangeVector)> ReadStoredAsync()
    {
        using var session = Store.OpenAsyncSession();
        var stored = await session.LoadAsync<Person>(Id);
        return (stored, session.Advanced.GetChangeVectorFor(stored));
    }

    private async Task AssertConflictAsync(SparkClientException ex)
    {
        ex.StatusCode.Should().Be(HttpStatusCode.Conflict);
        ex.ResponseBody.Should().Contain("Concurrency conflict");

        var (stored, changeVector) = await ReadStoredAsync();
        stored.LastName.Should().Be("Concurrent", "the concurrent writer's change must survive the refused save");
        stored.FirstName.Should().Be("Alice", "the refused save must write nothing");

        // R2-M1: RavenDB's own ConcurrencyException message carries change vectors; none may reach the client.
        ex.ResponseBody.Should().NotContain(changeVector);
        ChangeVectorPattern.IsMatch(ex.ResponseBody ?? string.Empty).Should().BeFalse("the 409 body must not leak a change vector");
    }

    [Fact]
    public async Task Save_with_etag_refuses_a_write_that_raced_past_the_check()
    {
        var po = await SeedAndLoadAsync();
        po.Etag.Should().NotBeNullOrEmpty();
        SetAttribute(po, "FirstName", "Alicia");

        _race.ArmSave();
        var ex = await Assert.ThrowsAsync<SparkClientException>(() => _client.UpdatePersistentObjectAsync(po));

        _race.Fired.Should().BeTrue("the concurrent writer must have landed for this test to mean anything");
        await AssertConflictAsync(ex);
    }

    [Fact]
    public async Task Internal_save_without_etag_still_protects_the_load_to_write_window()
    {
        // #467, D16: over HTTP an update without an etag is a 400 (UpdateEndpointConcurrencyTests). An
        // internal caller through IDatabaseAccess may still omit it — the internal overwrite — and then
        // the write carries the version the save loaded, so a write landing after that load is a 409.
        var po = await SeedAndLoadAsync();
        po.Etag = null;
        SetAttribute(po, "FirstName", "Alicia");

        _race.ArmSave();
        await using var scope = _factory.GetService<IServiceScopeFactory>().CreateAsyncScope();
        var databaseAccess = scope.ServiceProvider.GetRequiredService<IDatabaseAccess>();
        var ex = await Record.ExceptionAsync(() => databaseAccess.SavePersistentObjectAsync(po));

        _race.Fired.Should().BeTrue();
        ex.Should().BeOfType<MintPlayer.Spark.Exceptions.SparkConcurrencyException>();
        var (stored, _) = await ReadStoredAsync();
        stored.LastName.Should().Be("Concurrent", "the concurrent write survives");
    }

    [Fact]
    public async Task Hard_delete_refuses_a_write_that_raced_past_the_etag_check()
    {
        // #467, D14: the delete is written with the etag the caller sent, so a write landing after the
        // etag check — here, from a before-delete hook — still refuses it.
        var po = await SeedAndLoadAsync();

        _race.ArmDeleteRace();
        var ex = await Assert.ThrowsAsync<SparkClientException>(() => _client.DeletePersistentObjectAsync(po));

        _race.Fired.Should().BeTrue();
        await AssertConflictAsync(ex);
        var (stored, _) = await ReadStoredAsync();
        stored.LastName.Should().Be("Concurrent", "the row and the concurrent write survive");
    }

    [Fact]
    public async Task Save_without_a_race_still_succeeds()
    {
        var po = await SeedAndLoadAsync();
        SetAttribute(po, "FirstName", "Alicia");

        var saved = await _client.UpdatePersistentObjectAsync(po);

        saved.Etag.Should().NotBeNullOrEmpty();
        var (stored, changeVector) = await ReadStoredAsync();
        stored.FirstName.Should().Be("Alicia");
        saved.Etag.Should().Be(changeVector, "the returned etag is the change vector of the write");
    }

    [Fact]
    public async Task Replaced_delete_refuses_a_write_that_raced_past_the_gate()
    {
        await SeedAndLoadAsync();

        _race.ArmDelete();
        var ex = await Assert.ThrowsAsync<SparkClientException>(() => _client.DeleteAsLoadedAsync(PersonTypeId, Id));

        _race.Fired.Should().BeTrue();
        await AssertConflictAsync(ex);
    }

    [Fact]
    public async Task Replaced_delete_without_a_race_still_succeeds()
    {
        await SeedAndLoadAsync();

        _race.ReplaceDeletes = true;
        await _client.DeleteAsLoadedAsync(PersonTypeId, Id);

        var (stored, _) = await ReadStoredAsync();
        stored.FirstName.Should().Be("[deleted]", "the replacement was written");
    }

    public sealed class RaceSwitch(IDocumentStore store)
    {
        private bool saveArmed;
        private bool deleteArmed;

        public IDocumentStore Store { get; } = store;
        public bool Fired { get; private set; }
        public bool ReplaceDeletes { get; set; }

        public void ArmSave() => saveArmed = true;

        public void ArmDelete()
        {
            deleteArmed = true;
            ReplaceDeletes = true;
        }

        /// <summary>A concurrent write during a delete that is NOT replaced: the hard-delete path.</summary>
        public void ArmDeleteRace() => deleteArmed = true;

        public bool TakeSave()
        {
            var armed = saveArmed;
            saveArmed = false;
            return armed;
        }

        public bool TakeDelete()
        {
            var armed = deleteArmed;
            deleteArmed = false;
            return armed;
        }

        /// <summary>The other user's save: a separate session, committed before the request writes.</summary>
        public async Task WriteConcurrentlyAsync(string id)
        {
            using var session = Store.OpenAsyncSession();
            var person = await session.LoadAsync<Person>(id);
            person.LastName = "Concurrent";
            await session.SaveChangesAsync();
            Fired = true;
        }
    }

    public sealed class ConcurrentWriterInterceptor(RaceSwitch race) : IPersistentObjectInterceptor
    {
        public bool AppliesTo(Type entityType) => entityType == typeof(Person);

        public async ValueTask OnBeforeSaveAsync(SaveContext context)
        {
            if (race.TakeSave())
                await race.WriteConcurrentlyAsync(context.PersistentObject.Id!);
        }

        public async ValueTask OnBeforeDeleteAsync(DeleteContext context)
        {
            if (race.TakeDelete())
                await race.WriteConcurrentlyAsync(context.Id);
            if (race.ReplaceDeletes)
            {
                ((Person)context.Entity).FirstName = "[deleted]";
                context.Replace();
            }
        }
    }
}
