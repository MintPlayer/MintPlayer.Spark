using System.Net;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Spikes.Issue467;

/// <summary>
/// #467 spikes S4 (Read gate on delete-many, PRD D11) and S7 (query-level <c>OnDisableActionsAsync</c>
/// bypass, PRD D12).
/// </summary>
/// <remarks>
/// RED by design: these assert what the PRD decided. Tests marked CONTROL pin today's behaviour that
/// the spike relies on and are expected to pass.
/// </remarks>
public class Issue467DeleteManyGateTests : SparkTestDriver
{
    private I467Host _host = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _host = await I467Host.StartAsync(Store);
    }

    public override async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        await base.DisposeAsync();
    }

    private async Task<bool> ExistsAsync<T>(string id) where T : class
    {
        using var session = Store.OpenAsyncSession();
        return await session.LoadAsync<T>(id) is not null;
    }

    private Task SeedNotesAsync() => SeedAsync(async session =>
    {
        await session.StoreAsync(new I467Note { Id = "I467Notes/alice-1", Title = "mine", Owner = "alice" });
        await session.StoreAsync(new I467Note { Id = "I467Notes/bob-1", Title = "bobs", Owner = "bob" });
        await session.StoreAsync(new I467Note { Id = "I467Notes/bob-frozen", Title = "frozen", Owner = "bob" });
    });

    private static object DeleteNotes(params string[] ids)
        => Wire.Typed(I467Models.NoteTypeId, new { ids, queryId = I467Models.NotesQueryId.ToString() });

    // ---- S4 / D11 ---------------------------------------------------------------------------------

    // S4 CONTROL: the fixture is valid — bob's note is not readable by id (Read row rule), and the
    // single-row delete answers 404 because it pre-loads through the gated read (Delete.cs:43).
    [Fact]
    public async Task S4_control_a_row_hidden_by_the_Read_rule_is_404_on_load_and_on_single_delete()
    {
        await SeedNotesAsync();

        var (load, _) = await _host.SendAsync("/spark/po/load", Wire.Typed(I467Models.NoteTypeId, id: "I467Notes/bob-1"));
        load.Should().Be(HttpStatusCode.NotFound);

        var (delete, _) = await _host.SendAsync("/spark/po/delete", Wire.Typed(I467Models.NoteTypeId, id: "I467Notes/bob-1"));
        delete.Should().Be(HttpStatusCode.NotFound);
        (await ExistsAsync<I467Note>("I467Notes/bob-1")).Should().BeTrue();
    }

    // S4 / D11: delete-many must apply the Read gate too. Today it checks only Delete/T and the
    // Delete row rule (DatabaseAccess.cs:634,659), which is wider here, so bob's unreadable note goes.
    [Fact]
    public async Task S4_delete_many_of_a_row_the_caller_cannot_read_is_404_and_deletes_nothing()
    {
        await SeedNotesAsync();

        var (status, body) = await _host.SendAsync("/spark/po/delete-many", DeleteNotes("I467Notes/alice-1", "I467Notes/bob-1"));

        (await ExistsAsync<I467Note>("I467Notes/bob-1")).Should().BeTrue("D11: a row the caller cannot read must not be deletable in bulk");
        (await ExistsAsync<I467Note>("I467Notes/alice-1")).Should().BeTrue("D11: all or nothing — the batch is refused");
        status.Should().Be(HttpStatusCode.NotFound, $"D11: an unreadable row counts as missing (M-3); body: {body}");
    }

    // S4 / D11 + M-3: the existence oracle. An unreadable row whose object-level hook withholds Delete
    // answers 403 today (the disabled-action gate runs on rows the caller cannot read), while a missing
    // id answers 404 — so the status tells the caller the hidden row exists.
    [Fact]
    public async Task S4_delete_many_answers_an_unreadable_row_exactly_like_a_missing_row()
    {
        await SeedNotesAsync();

        var (hidden, hiddenBody) = await _host.SendAsync("/spark/po/delete-many", DeleteNotes("I467Notes/bob-frozen"));
        var (missing, _) = await _host.SendAsync("/spark/po/delete-many", DeleteNotes("I467Notes/does-not-exist"));

        (await ExistsAsync<I467Note>("I467Notes/bob-frozen")).Should().BeTrue();
        missing.Should().Be(HttpStatusCode.NotFound);
        hidden.Should().Be(missing, $"D11/M-3: an unreadable row must be indistinguishable from a missing one; body: {hiddenBody}");
    }

    // ---- S7 / D12 ---------------------------------------------------------------------------------

    private Task SeedTasksAsync() => SeedAsync(async session =>
    {
        await session.StoreAsync(new I467Task { Id = "I467Tasks/locked-1", Title = "locked", Locked = true });
        await session.StoreAsync(new I467Task { Id = "I467Tasks/open-1", Title = "open", Locked = false });
    });

    // S7 CONTROL: through the query the rows came from, the query-level disable refuses (403).
    [Fact]
    public async Task S7_control_delete_many_through_the_locked_query_is_refused()
    {
        await SeedTasksAsync();

        var (status, _) = await _host.SendAsync("/spark/po/delete-many",
            Wire.Typed(I467Models.TaskTypeId, new { ids = new[] { "I467Tasks/locked-1" }, queryId = I467Models.LockedTasksQueryId.ToString() }));

        status.Should().Be(HttpStatusCode.Forbidden);
        (await ExistsAsync<I467Task>("I467Tasks/locked-1")).Should().BeTrue();
    }

    // S7 / D12: delete-many without a queryId is a 400. Today the query target is simply not judged
    // (DatabaseAccess.cs:669-681) and the rows are deleted.
    [Fact]
    public async Task S7_delete_many_without_queryId_is_400_and_deletes_nothing()
    {
        await SeedTasksAsync();

        var (status, body) = await _host.SendAsync("/spark/po/delete-many",
            Wire.Typed(I467Models.TaskTypeId, new { ids = new[] { "I467Tasks/locked-1" } }));

        (await ExistsAsync<I467Task>("I467Tasks/locked-1")).Should().BeTrue("D12: OnDisableActionsAsync cannot be skipped by omitting queryId");
        status.Should().Be(HttpStatusCode.BadRequest, $"D12: queryId is required on bulk calls; body: {body}");
    }

    // S7 / D12: naming another query of the same type. D12 fetches the rows THROUGH the named query;
    // the locked row is not in the open query, so it counts as missing (404, all-or-nothing).
    [Fact]
    public async Task S7_delete_many_naming_another_query_of_the_same_type_is_refused_and_deletes_nothing()
    {
        await SeedTasksAsync();

        var (status, body) = await _host.SendAsync("/spark/po/delete-many",
            Wire.Typed(I467Models.TaskTypeId, new { ids = new[] { "I467Tasks/locked-1" }, queryId = I467Models.OpenTasksQueryId.ToString() }));

        (await ExistsAsync<I467Task>("I467Tasks/locked-1")).Should().BeTrue("D12: naming another query must not bypass the locked query's disable");
        status.Should().Be(HttpStatusCode.NotFound, $"D12: rows are fetched through the named query; one not in it is missing; body: {body}");
    }
}
