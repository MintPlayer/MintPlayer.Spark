using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Abstractions.Model;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.History;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Contributions F1 and F2 — <see cref="IPersistentObjectInterceptor.OnAfterMaterializeAsync"/> fills a
/// satellite (<c>[JsonIgnore]</c>d, side-document-backed) AsDetail collection at every load path, and
/// a History revert or a full replication sync never posts that attribute, so neither withdraws its rows.
/// </summary>
/// <remarks>
/// The interceptor here is a miniature of what the Contributions library will do: rows live in side
/// documents <c>{songId}/lines/{slot}</c>, are hydrated after materialize and written back (added,
/// updated, removed) in the before-save hook. A withdrawn row is therefore a deleted side document —
/// the observable the F2 tests check. Fixture names start with <c>Mz</c> because ActionsResolver
/// matches actions classes by simple name across the assembly.
/// </remarks>
public class MaterializeInterceptorTests : SparkTestDriver
{
    private static readonly Guid SongTypeId = Guid.Parse("4d7a0000-0000-4000-8000-4d7a00000001");
    private static readonly Guid LineTypeId = Guid.Parse("4d7a0000-0000-4000-8000-4d7a00000002");
    private const string SongId = "MzSongs/1";

    static MaterializeInterceptorTests()
        => SparkValueObjects.Register(typeof(MzLine), nameof(MzLine.Key), row => ((MzLine)row).Key);

    private readonly List<IAsyncDisposable> factories = [];

    public override async Task DisposeAsync()
    {
        foreach (var factory in factories)
            await factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private async Task<Host> StartAsync(SparkTestSecurity? security = null)
    {
        var factory = new SparkEndpointFactory<MzContext>(
            Store,
            [SongModel(), LineModel()],
            configureServices: services =>
            {
                services.AddSingleton<MzRecorder>();
                services.AddScoped<MzSongActions>();
                services.AddScoped<MzLineActions>();
            },
            configureSpark: spark =>
            {
                spark.AddHistory();
                spark.AddInterceptor<MzHydratingInterceptor>();
            },
            security: security ?? SparkTestSecurity.Permissive);
        factories.Add(factory);
        var (cookie, xsrf) = await factory.MintAntiforgeryAsync();
        return new Host(factory, factory.CreateClient(), cookie, xsrf);
    }

    /// <summary>A song whose two lines live only in side documents.</summary>
    private async Task SeedAsync(string title = "v1")
    {
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(new MzSong { Title = title }, SongId);
        await session.StoreAsync(new MzLineDoc { Slot = 1, Text = "one" }, $"{SongId}/lines/1");
        await session.StoreAsync(new MzLineDoc { Slot = 2, Text = "two" }, $"{SongId}/lines/2");
        await session.SaveChangesAsync();
    }

    private async Task<List<string>> SideDocumentsAsync()
    {
        using var session = Store.OpenAsyncSession();
        var docs = await session.Advanced.LoadStartingWithAsync<MzLineDoc>($"{SongId}/lines/", pageSize: 1024);
        return [.. docs.OrderBy(d => d.Slot).Select(d => $"{d.Slot}:{d.Text}")];
    }

    // ---- F1: hydration ----------------------------------------------------------------------------

    [Fact]
    public async Task F1_a_GET_shows_the_hydrated_rows_and_hooks_the_entity_once()
    {
        var host = await StartAsync();
        await SeedAsync();

        var (status, body) = await host.SendAsync("/spark/po/load", Wire.Typed(SongTypeId, id: SongId));

        status.Should().Be(HttpStatusCode.OK);
        var rows = Attribute(body, "Lines").GetProperty("objects").EnumerateArray()
            .Select(r => (Key: r.GetProperty("id").GetString(), Text: RowText(r)))
            .ToList();
        rows.Should().Equal(("1", "one"), ("2", "two"));
        host.Recorder.Calls.Should().Equal($"Load:{SongId}");
    }

    [Fact]
    public async Task F1_an_Update_that_edits_a_hydrated_row_is_an_Edit_not_a_New_plus_a_Delete()
    {
        // Edit on the row type, but neither New nor Delete: were the stored rows not hydrated before
        // the merge, the posted rows would match nothing and the save would demand New (and Delete).
        var host = await StartAsync(SparkTestSecurity.Permissive.Denying("New/MzLine", "Delete/MzLine"));
        await SeedAsync();
        var (_, loaded) = await host.SendAsync("/spark/po/load", Wire.Typed(SongTypeId, id: SongId));
        host.Recorder.Clear();

        var po = JsonNode.Parse(loaded.GetRawText())!.AsObject();
        var lines = po["attributes"]!.AsArray().Single(a => (string?)a!["name"] == "Lines")!;
        lines["isValueChanged"] = true;
        var first = lines["objects"]!.AsArray().Single(r => (string?)r!["id"] == "1")!;
        var text = first["attributes"]!.AsArray().Single(a => (string?)a!["name"] == "Text")!;
        text["value"] = "one (edited)";
        text["isValueChanged"] = true;

        var (status, _) = await host.SendAsync("/spark/po/update", Wire.Typed(SongTypeId, new { persistentObject = po }, SongId));

        status.Should().Be(HttpStatusCode.OK);
        (await SideDocumentsAsync()).Should().Equal(["1:one (edited)", "2:two"],
            "the edited row was written back and the untouched one was not dropped");

        // The pre-read hooked the request session's instance; the save reload got that same tracked
        // instance back and did not hook it again. The Before snapshot is a separate side-session load.
        host.Recorder.Calls.Should().Equal($"Load:{SongId}", $"Before:{SongId}");
        host.Recorder.BeforeRows.Should().Equal(["one", "two"], "SaveContext.Before carries the hydrated rows");
    }

    [Fact]
    public async Task F1_the_save_reload_hydrates_when_nothing_pre_read_the_entity()
    {
        var host = await StartAsync(SparkTestSecurity.Permissive.Denying("New/MzLine", "Delete/MzLine"));
        await SeedAsync();

        using (var scope = host.Factory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IDatabaseAccess>();
            var po = new Abstractions.PersistentObject { Id = SongId, ObjectTypeId = SongTypeId, Name = "MzSong" };
            po.AddAttribute(new PersistentObjectAttribute { Name = "Title", DataType = "string", Value = "renamed", IsValueChanged = true });
            await db.SavePersistentObjectAsync(po);
        }

        host.Recorder.Calls.Should().Equal($"Before:{SongId}", $"SaveReload:{SongId}");
        host.Recorder.BeforeRows.Should().Equal(["one", "two"]);
        (await SideDocumentsAsync()).Should().HaveCount(2, "the rows the reload hydrated are written back as they were");
    }

    [Fact]
    public async Task F1_the_stored_document_never_contains_the_satellite_property()
    {
        var host = await StartAsync();
        await SeedAsync();
        var (_, loaded) = await host.SendAsync("/spark/po/load", Wire.Typed(SongTypeId, id: SongId));
        var po = JsonNode.Parse(loaded.GetRawText())!.AsObject();
        var title = po["attributes"]!.AsArray().Single(a => (string?)a!["name"] == "Title")!;
        title["value"] = "v2";
        title["isValueChanged"] = true;

        (await host.SendAsync("/spark/po/update", Wire.Typed(SongTypeId, new { persistentObject = po }, SongId))).Status
            .Should().Be(HttpStatusCode.OK);

        using var session = Store.OpenAsyncSession();
        var raw = await session.LoadAsync<MzSongRaw>(SongId);
        raw.Title.Should().Be("v2");
        raw.Lines.Should().BeNull("[JsonIgnore] keeps the hydrated rows out of the document");
    }

    // ---- F2: replayed writes never post a satellite -----------------------------------------------

    [Fact]
    public async Task F2_a_History_revert_does_not_withdraw_the_satellite_rows()
    {
        var host = await StartAsync();
        await SeedAsync("v1");
        var v1 = await ChangeVectorAsync();
        using (var session = Store.OpenAsyncSession())
        {
            (await session.LoadAsync<MzSong>(SongId)).Title = "v2";
            await session.SaveChangesAsync();
        }

        var (status, _) = await host.SendAsync("/spark/po/revert", Wire.Typed(SongTypeId, new { changeVector = v1 }, SongId));

        status.Should().Be(HttpStatusCode.OK);
        using (var session = Store.OpenAsyncSession())
            (await session.LoadAsync<MzSong>(SongId)).Title.Should().Be("v1");
        (await SideDocumentsAsync()).Should().Equal(["1:one", "2:two"],
            "the revision never held the rows, so the revert must not post them as empty");
    }

    [Fact]
    public async Task F2_a_full_sync_without_the_satellite_does_not_clear_it()
    {
        var host = await StartAsync();
        await SeedAsync();

        using (var scope = host.Factory.CreateScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<ISyncActionHandler>();
            await handler.HandleSaveAsync("MzSongs", SongId, new Dictionary<string, object?> { ["Title"] = "synced" }, properties: null);
        }

        using (var session = Store.OpenAsyncSession())
            (await session.LoadAsync<MzSong>(SongId)).Title.Should().Be("synced");
        (await SideDocumentsAsync()).Should().HaveCount(2, "a full sync cannot carry a satellite, so it must not post it as empty");
    }

    private async Task<string> ChangeVectorAsync()
    {
        using var session = Store.OpenAsyncSession();
        var song = await session.LoadAsync<MzSong>(SongId);
        return session.Advanced.GetChangeVectorFor(song);
    }

    private static JsonElement Attribute(JsonElement body, string name) =>
        body.GetProperty("attributes").EnumerateArray()
            .Single(a => a.GetProperty("name").GetString() == name);

    private static string? RowText(JsonElement row) =>
        row.GetProperty("attributes").EnumerateArray()
            .Single(a => a.GetProperty("name").GetString() == "Text")
            .GetProperty("value").GetString();

    private static EntityTypeFile SongModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = SongTypeId,
            Name = "MzSong",
            ClrType = typeof(MzSong).FullName!,
            Breadcrumb = "{Title}",
            Revisions = new EntityRevisionsDefinition { Enabled = true },
            Attributes =
            [
                new() { Id = Guid.NewGuid(), Name = "Title", DataType = "string", IsVisible = true },
                new() { Id = Guid.NewGuid(), Name = "Lines", DataType = "AsDetail", AsDetailType = typeof(MzLine).FullName, IsArray = true, IsVisible = true },
            ],
        },
    };

    private static EntityTypeFile LineModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = LineTypeId,
            Name = "MzLine",
            ClrType = typeof(MzLine).FullName!,
            Breadcrumb = "{Text}",
            Attributes =
            [
                new() { Id = Guid.NewGuid(), Name = "Slot", DataType = "number", IsVisible = true },
                new() { Id = Guid.NewGuid(), Name = "Text", DataType = "string", IsVisible = true },
            ],
        },
    };

    private sealed record Host(SparkEndpointFactory<MzContext> Factory, HttpClient Client, string Cookie, string Xsrf)
    {
        public MzRecorder Recorder => Factory.GetService<MzRecorder>();

        public async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(string url, object payload)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
            request.Headers.Add("Cookie", Cookie);
            request.Headers.Add("X-XSRF-TOKEN", Xsrf);
            var response = await Client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            return (response.StatusCode, string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone());
        }
    }
}

public class MzSong
{
    public string? Id { get; set; }
    public string Title { get; set; } = string.Empty;

    /// <summary>A satellite: modelled, never stored in this document — the interceptor supplies it.</summary>
    [Newtonsoft.Json.JsonIgnore]
    public List<MzLine> Lines { get; set; } = [];
}

/// <summary>The same document, read with a field that would expose a stored <c>Lines</c>.</summary>
public class MzSongRaw
{
    public string? Id { get; set; }
    public string? Title { get; set; }
    public object? Lines { get; set; }
}

/// <summary>A row with a deterministic, get-only key (PRD §4.1 S-C3) — no Guid initializer.</summary>
public class MzLine
{
    public string Key => Slot.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public int Slot { get; set; }
    public string Text { get; set; } = string.Empty;
}

public class MzLineDoc
{
    public string? Id { get; set; }
    public int Slot { get; set; }
    public string Text { get; set; } = string.Empty;
}

public class MzContext : SparkContext
{
    public Raven.Client.Documents.Linq.IRavenQueryable<MzSong> Songs => Session.Query<MzSong>();
}

public class MzSongActions(IEntityMapper mapper) : DefaultPersistentObjectActions<MzSong>(mapper), ISparkOwnsRowSecurity
{
    public string RowSecurityRationale => "Test fixture: every song is seeded by the test that reads it.";
}

public class MzLineActions(IEntityMapper mapper) : DefaultPersistentObjectActions<MzLine>(mapper), ISparkOwnsRowSecurity
{
    public string RowSecurityRationale => "Test fixture: lines are reachable only through their song.";
}

public sealed class MzRecorder
{
    public ConcurrentQueue<string> CallQueue { get; } = new();
    public List<string> Calls => [.. CallQueue];
    public List<string> BeforeRows { get; set; } = [];

    public void Clear()
    {
        CallQueue.Clear();
        BeforeRows = [];
    }
}

/// <summary>Hydrates <see cref="MzSong.Lines"/> from side documents and writes it back on save.</summary>
public sealed class MzHydratingInterceptor(MzRecorder recorder, IAsyncDocumentSession session) : IAfterMaterialize, IBeforeSave
{
    public bool AppliesTo(Type entityType) => entityType == typeof(MzSong);

    public async ValueTask OnAfterMaterializeAsync(MaterializeContext context)
    {
        var song = (MzSong)context.Entity;
        var loadSession = context.GetSession();
        var id = loadSession.Advanced.GetDocumentId(song);
        recorder.CallQueue.Enqueue($"{context.Reason}:{id}");

        var docs = await loadSession.Advanced.LoadStartingWithAsync<MzLineDoc>($"{id}/lines/", pageSize: 1024);
        song.Lines = [.. docs.OrderBy(d => d.Slot).Select(d => new MzLine { Slot = d.Slot, Text = d.Text })];
    }

    public async ValueTask OnBeforeSaveAsync(SaveContext context)
    {
        if (context.Before is MzSong before)
            recorder.BeforeRows = [.. before.Lines.Select(l => l.Text)];

        var song = (MzSong)context.Entity!;
        var id = song.Id ?? context.PersistentObject.Id;
        if (string.IsNullOrEmpty(id))
            return;

        var stored = await session.Advanced.LoadStartingWithAsync<MzLineDoc>($"{id}/lines/", pageSize: 1024);
        foreach (var doc in stored.Where(d => song.Lines.All(l => l.Slot != d.Slot)))
            session.Delete(doc);
        foreach (var line in song.Lines)
        {
            if (stored.FirstOrDefault(d => d.Slot == line.Slot) is { } doc)
                doc.Text = line.Text;
            else
                await session.StoreAsync(new MzLineDoc { Slot = line.Slot, Text = line.Text }, $"{id}/lines/{line.Slot}");
        }
    }
}
