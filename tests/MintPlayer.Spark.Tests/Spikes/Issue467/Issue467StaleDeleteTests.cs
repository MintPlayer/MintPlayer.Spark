using System.Net;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;
using Xunit.Abstractions;

namespace MintPlayer.Spark.Tests.Spikes.Issue467;

/// <summary>
/// #467 spike S5 — (a) the stale-grid hard-delete race (PRD D14) and (b) which change vector RavenDB
/// hands out for a projected query result (D14 needs the document's, on <c>QueryResultItem.etag</c>).
/// </summary>
/// <remarks>
/// (a) was RED by design until M6: neither delete request carried an etag, so a delete was
/// last-write-wins. Both now carry one (<c>etag</c> on <c>/po/delete</c>, <c>items[].etag</c> on
/// <c>/po/delete-many</c>), taken from the load or the query row. (b) is a probe: it records what
/// Raven returns.
/// </remarks>
public class Issue467StaleDeleteTests(ITestOutputHelper output) : SparkTestDriver
{
    private const string Id = "I467Items/1";

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

    private Task SeedItemAsync() => SeedAsync(session => session.StoreAsync(new I467Item { Id = Id, Name = "item", Remark = "original" }));

    /// <summary>User A's edit, landing after user B loaded the grid.</summary>
    private async Task EditConcurrentlyAsync()
    {
        using var session = Store.OpenAsyncSession();
        var item = await session.LoadAsync<I467Item>(Id);
        item.Remark = "edited by A";
        await session.SaveChangesAsync();
    }

    private async Task<I467Item?> LoadStoredAsync()
    {
        using var session = Store.OpenAsyncSession();
        return await session.LoadAsync<I467Item>(Id);
    }

    // ---- S5 (a) / D14 -----------------------------------------------------------------------------

    // S5a / D14: a single delete with a stale etag is a 409 and the edited row survives.
    // Intended shape (etag on the reference request); today the field is ignored → 204, row gone.
    [Fact]
    public async Task S5a_single_delete_with_a_stale_etag_is_409_and_the_concurrent_edit_survives()
    {
        await SeedItemAsync();
        var etag = await _host.LoadEtagAsync(I467Models.ItemTypeId, Id);
        etag.Should().NotBeNullOrEmpty("the detail load hands out an etag today");
        await EditConcurrentlyAsync();

        var (status, body) = await _host.SendAsync("/spark/po/delete", Wire.Typed(I467Models.ItemTypeId, new { etag }, Id));

        (await LoadStoredAsync()).Should().NotBeNull("D14: a delete from a stale view must not remove a row edited since");
        status.Should().Be(HttpStatusCode.Conflict, $"D14: an etag mismatch is a 409; body: {body}");
    }

    // S5a / D14: a single delete without an etag is a 400. Today: 204, deleted (last-write-wins).
    [Fact]
    public async Task S5a_single_delete_without_etag_is_400_and_deletes_nothing()
    {
        await SeedItemAsync();

        var (status, body) = await _host.SendAsync("/spark/po/delete", Wire.Typed(I467Models.ItemTypeId, id: Id));

        (await LoadStoredAsync()).Should().NotBeNull("D14: a delete without an etag is refused");
        status.Should().Be(HttpStatusCode.BadRequest, $"D14: a missing etag is a 400; body: {body}");
    }

    /// <summary>The etag of <see cref="Id"/>'s row as the grid lists it (<c>QueryResultItem.Etag</c>).</summary>
    private async Task<string?> ListedEtagAsync()
    {
        var (status, body) = await _host.SendAsync("/spark/queries/execute", Wire.Query(I467Models.ItemsQueryId));
        status.Should().Be(HttpStatusCode.OK, body);
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var root = doc.RootElement.TryGetProperty("result", out var result) ? result : doc.RootElement;
        return root.GetProperty("items").EnumerateArray()
            .Where(item => item.GetProperty("id").GetString() == Id)
            .Select(item => item.TryGetProperty("etag", out var etag) ? etag.GetString() : null)
            .Single();
    }

    // S5a / D14: the grid's row carries the document's change vector, so a delete from the list can
    // say which version it saw.
    [Fact]
    public async Task S5a_a_query_row_carries_the_documents_change_vector()
    {
        await SeedItemAsync();
        await EditConcurrentlyAsync();

        string stored;
        using (var session = Store.OpenAsyncSession())
            stored = session.Advanced.GetChangeVectorFor(await session.LoadAsync<I467Item>(Id));

        (await ListedEtagAsync()).Should().Be(stored);
    }

    // S5a / D14: delete-many from a stale grid. The row was edited after the grid loaded; the request
    // names the version the grid showed, so it is a 409 that names the row, and the edit survives.
    [Fact]
    public async Task S5a_delete_many_after_a_concurrent_edit_is_409_naming_the_row_and_the_edit_survives()
    {
        await SeedItemAsync();
        var listed = await ListedEtagAsync();
        await EditConcurrentlyAsync();

        var (status, body) = await _host.SendAsync("/spark/po/delete-many", Wire.Typed(I467Models.ItemTypeId, new
        {
            items = new[] { new { id = Id, etag = listed } },
            queryId = I467Models.ItemsQueryId.ToString(),
        }));

        (await LoadStoredAsync())!.Remark.Should().Be("edited by A", "D14: a delete from a stale list must not remove a row edited since");
        status.Should().Be(HttpStatusCode.Conflict, $"D14: an etag mismatch is a 409; body: {body}");
        body.Should().Contain("item", "D18: the refusal names the changed row by its breadcrumb");
    }

    // S5a / D14: a row of a delete-many without its etag is a 400, and nothing is deleted.
    [Fact]
    public async Task S5a_delete_many_without_etags_is_400_and_deletes_nothing()
    {
        await SeedItemAsync();

        var (status, body) = await _host.SendAsync("/spark/po/delete-many", Wire.Typed(I467Models.ItemTypeId, new
        {
            items = new[] { new { id = Id } },
            queryId = I467Models.ItemsQueryId.ToString(),
        }));

        (await LoadStoredAsync()).Should().NotBeNull();
        status.Should().Be(HttpStatusCode.BadRequest, $"D14: a delete without per-id etags is a 400; body: {body}");
    }

    // S5a / D14: an unchanged row deletes, with the etag the list showed.
    [Fact]
    public async Task S5a_control_delete_many_with_the_listed_etag_deletes()
    {
        await SeedItemAsync();
        var listed = await ListedEtagAsync();

        var (status, body) = await _host.SendAsync("/spark/po/delete-many", Wire.Typed(I467Models.ItemTypeId, new
        {
            items = new[] { new { id = Id, etag = listed } },
            queryId = I467Models.ItemsQueryId.ToString(),
        }));

        status.Should().Be(HttpStatusCode.NoContent, body);
        (await LoadStoredAsync()).Should().BeNull();
    }

    // ---- S5 (b) projection probe -------------------------------------------------------------------

    public class I467ItemsIndex : AbstractIndexCreationTask<I467Item>
    {
        public I467ItemsIndex()
        {
            Map = items => from i in items select new { i.Name, i.Remark };
            StoreAllFields(FieldStorage.Yes);
        }
    }

    public class I467ItemView
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Remark { get; set; }
    }

    // S5b / D14: does a projected result carry the DOCUMENT's change vector? Records every variant;
    // asserts the variant D14 needs (stored-field index projection, streamed with metadata).
    [Fact]
    public async Task S5b_probe_projection_change_vector_vs_document_change_vector()
    {
        await SeedItemAsync();
        await EditConcurrentlyAsync(); // a second version, so a stale/index-entry CV would differ
        await new I467ItemsIndex().ExecuteAsync(Store);
        await WaitForIndexesAsync();

        string documentCv;
        using (var session = Store.OpenAsyncSession())
        {
            var doc = await session.LoadAsync<I467Item>(Id);
            documentCv = session.Advanced.GetChangeVectorFor(doc);
        }
        output.WriteLine($"document CV                                  : {documentCv}");

        var observed = new Dictionary<string, string?>();

        // 1. Index projection (stored fields), materialised: is the projected object tracked?
        using (var session = Store.OpenAsyncSession())
        {
            var list = await session.Query<I467Item, I467ItemsIndex>().ProjectInto<I467ItemView>().ToListAsync();
            observed["index ProjectInto, GetChangeVectorFor"] = Try(() => session.Advanced.GetChangeVectorFor(list[0]));
            observed["index ProjectInto, metadata @change-vector"] = Try(() => session.Advanced.GetMetadataFor(list[0]).TryGetValue("@change-vector", out var cv) ? cv?.ToString() : "(absent)");
        }

        // 2. Index projection, streamed: StreamResult carries ChangeVector + Metadata per result.
        using (var session = Store.OpenAsyncSession())
        {
            var query = session.Query<I467Item, I467ItemsIndex>().ProjectInto<I467ItemView>();
            await using var stream = await session.Advanced.StreamAsync(query);
            while (await stream.MoveNextAsync())
            {
                observed["index ProjectInto, stream ChangeVector"] = stream.Current.ChangeVector ?? "(null)";
                observed["index ProjectInto, stream metadata @change-vector"] =
                    stream.Current.Metadata.TryGetValue("@change-vector", out var cv) ? cv?.ToString() : "(absent)";
            }
        }

        // 3. Collection (auto-index) query with a select projection, materialised and streamed.
        using (var session = Store.OpenAsyncSession())
        {
            var list = await session.Query<I467Item>().Select(i => new I467ItemView { Id = i.Id, Name = i.Name }).ToListAsync();
            observed["collection Select, GetChangeVectorFor"] = Try(() => session.Advanced.GetChangeVectorFor(list[0]));

            var query = session.Query<I467Item>().Select(i => new I467ItemView { Id = i.Id, Name = i.Name });
            await using var stream = await session.Advanced.StreamAsync(query);
            while (await stream.MoveNextAsync())
                observed["collection Select, stream ChangeVector"] = stream.Current.ChangeVector ?? "(null)";
        }

        // 4. Full-entity index query (no projection): the baseline.
        using (var session = Store.OpenAsyncSession())
        {
            var list = await session.Query<I467Item, I467ItemsIndex>().ToListAsync();
            observed["index full entity, GetChangeVectorFor"] = Try(() => session.Advanced.GetChangeVectorFor(list[0]));
        }

        foreach (var (key, value) in observed)
            output.WriteLine($"{key,-45}: {value}{(value == documentCv ? "   (== document CV)" : string.Empty)}");

        var summary = string.Join(" | ", observed.Select(kv => $"{kv.Key} = {kv.Value}"));
        observed["index ProjectInto, stream ChangeVector"].Should().Be(documentCv,
            $"D14 needs the document's change vector on a projected row; document CV = {documentCv}; observed: {summary}");
    }

    private static string? Try(Func<string?> probe)
    {
        try { return probe() ?? "(null)"; }
        catch (Exception ex) { return $"throws {ex.GetType().Name}: {ex.Message.Split('\n')[0]}"; }
    }
}
