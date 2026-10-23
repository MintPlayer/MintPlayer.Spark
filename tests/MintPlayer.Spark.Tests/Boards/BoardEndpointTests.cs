using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MintPlayer.Spark.Boards;
using MintPlayer.Spark.Tests.Organizations;

namespace MintPlayer.Spark.Tests.Boards;

/// <summary>
/// <c>/spark/boards</c> (<c>docs/dashboard_layout_PRD.md</c> M2–M4) over RavenDB. The organizations host signs users
/// in by header; <c>OgNotes</c> is readable by signed-in users only, so it is the tile an anonymous visitor must not see.
/// </summary>
public class BoardEndpointTests : OrganizationsTestHost
{
    public BoardEndpointTests()
    {
        ExtraSpark = spark => spark.AddBoards(o => o.Boards.Add(new SparkBoardDefinition
        {
            Id = "home",
            Tiles =
            [
                new SparkBoardTile { Id = "notes", Kind = "query", Target = new() { ["query"] = "OgNotes" } },
                new SparkBoardTile { Id = "note-count", Kind = "count", Target = new() { ["query"] = "OgNotes" } },
                new SparkBoardTile { Id = "gone", Kind = "query", Target = new() { ["query"] = "NoSuchQuery" } },
            ],
            Layouts = new()
            {
                [2] =
                [
                    new("notes", new SparkTilePosition(1, 1, 1, 1)),
                    new("note-count", new SparkTilePosition(2, 1, 1, 1)),
                    new("gone", new SparkTilePosition(1, 2, 2, 1)),
                ],
            },
        }));
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> RequestAsync(HttpMethod method, string url, string? userId, object? payload = null)
    {
        using var client = Factory.CreateClient();
        var (cookie, xsrf) = await MintAsync(client, userId);
        var request = new HttpRequestMessage(method, url);
        if (payload is not null)
            request.Content = JsonContent.Create(payload);
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("X-XSRF-TOKEN", xsrf);
        if (userId is not null)
            request.Headers.Add(OgPrincipalStartupFilter.Header, userId);
        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static List<string> TileIds(JsonElement board)
        => board.GetProperty("tiles").EnumerateArray().Select(t => t.GetProperty("id").GetString()!).ToList();

    private static List<string> PlacedIds(JsonElement board, int count)
        => board.GetProperty("layouts").GetProperty(count.ToString()).EnumerateArray().Select(e => e.GetProperty("id").GetString()!).ToList();

    private static object Layout(params (string Id, int Col, int Row)[] entries)
        => entries.Select(e => new { id = e.Id, position = new { colStart = e.Col, rowStart = e.Row, colSpan = 1, rowSpan = 1 } }).ToArray();

    [Fact]
    public async Task An_anonymous_visitor_reads_the_default_without_the_tiles_they_cannot_see_and_cannot_save()
    {
        var (status, board) = await RequestAsync(HttpMethod.Get, "/spark/boards/home", null);

        status.Should().Be(HttpStatusCode.OK);
        TileIds(board).Should().BeEmpty();
        PlacedIds(board, 2).Should().BeEmpty();
        board.GetProperty("canCustomize").GetBoolean().Should().BeFalse();

        (await RequestAsync(HttpMethod.Put, "/spark/boards/home", null, new { tiles = Array.Empty<object>() })).Status.Should().Be(HttpStatusCode.Unauthorized);
        (await RequestAsync(HttpMethod.Get, "/spark/boards/home/catalog", null)).Status.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_undeclared_board_is_not_found()
        => (await RequestAsync(HttpMethod.Get, "/spark/boards/nope", "SparkUsers/ann")).Status.Should().Be(HttpStatusCode.NotFound);

    [Fact]
    public async Task A_signed_in_user_sees_the_tiles_they_may_open_and_a_tile_with_a_missing_target_is_hidden()
    {
        var ann = await SeedUserAsync("ann");

        var (status, board) = await RequestAsync(HttpMethod.Get, "/spark/boards/home", ann.Id);

        status.Should().Be(HttpStatusCode.OK);
        TileIds(board).Should().Equal("notes", "note-count");
        PlacedIds(board, 2).Should().Equal("notes", "note-count");
        board.GetProperty("canCustomize").GetBoolean().Should().BeTrue();
        board.GetProperty("customized").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task A_saved_layout_is_the_users_own_and_another_user_still_sees_the_default()
    {
        var ann = await SeedUserAsync("ann");
        var bob = await SeedUserAsync("bob");

        var (status, saved) = await RequestAsync(HttpMethod.Put, "/spark/boards/home", ann.Id, new
        {
            removed = new[] { "note-count" },
            titles = new Dictionary<string, string> { ["notes"] = "My notes" },
            tiles = new[] { new { id = "u-1", kind = "count", target = new Dictionary<string, string> { ["query"] = "OgNotes" } } },
            layouts = new Dictionary<string, object> { ["2"] = Layout(("u-1", 1, 1), ("notes", 2, 1)) },
        });

        status.Should().Be(HttpStatusCode.OK);
        TileIds(saved).Should().Equal("notes", "u-1");
        PlacedIds(saved, 2).Should().Equal("u-1", "notes");
        saved.GetProperty("tiles")[0].GetProperty("customTitle").GetString().Should().Be("My notes");
        // The removal comes back, so the next save can state it again.
        saved.GetProperty("removed").EnumerateArray().Select(t => t.GetProperty("id").GetString()).Should().Equal("note-count");

        var stored = await LoadAsync<BoardLayout>(BoardLayout.IdFor(ann.Id!, "home"));
        stored!.Owner.Should().Be(ann.Id);
        // The hidden default's position is kept, though Ann never saw it.
        stored.Layouts[2].Select(e => e.Id).Should().Contain("gone");

        var (_, bobs) = await RequestAsync(HttpMethod.Get, "/spark/boards/home", bob.Id);
        TileIds(bobs).Should().Equal("notes", "note-count");
        bobs.GetProperty("customized").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task A_save_from_a_stale_read_is_a_conflict_and_reset_restores_the_default()
    {
        var ann = await SeedUserAsync("ann");
        var (_, first) = await RequestAsync(HttpMethod.Put, "/spark/boards/home", ann.Id, new { removed = new[] { "notes" } });
        var changeVector = first.GetProperty("changeVector").GetString();
        changeVector.Should().NotBeNullOrEmpty();

        (await RequestAsync(HttpMethod.Put, "/spark/boards/home", ann.Id, new { changeVector, removed = new[] { "notes", "note-count" } }))
            .Status.Should().Be(HttpStatusCode.OK);
        (await RequestAsync(HttpMethod.Put, "/spark/boards/home", ann.Id, new { changeVector, removed = Array.Empty<string>() }))
            .Status.Should().Be(HttpStatusCode.Conflict);
        // A first save against an existing layout is stale too.
        (await RequestAsync(HttpMethod.Put, "/spark/boards/home", ann.Id, new { removed = Array.Empty<string>() }))
            .Status.Should().Be(HttpStatusCode.Conflict);

        var (status, reset) = await RequestAsync(HttpMethod.Delete, "/spark/boards/home", ann.Id);
        status.Should().Be(HttpStatusCode.OK);
        TileIds(reset).Should().Equal("notes", "note-count");
        (await LoadAsync<BoardLayout>(BoardLayout.IdFor(ann.Id!, "home"))).Should().BeNull();
    }

    [Fact]
    public async Task A_save_cannot_add_a_tile_on_a_target_the_user_cannot_open()
    {
        var ann = await SeedUserAsync("ann");

        var (status, body) = await RequestAsync(HttpMethod.Put, "/spark/boards/home", ann.Id, new
        {
            tiles = new[] { new { id = "u-1", kind = "query", target = new Dictionary<string, string> { ["query"] = "NoSuchQuery" } } },
        });

        status.Should().Be(HttpStatusCode.BadRequest);
        body.GetProperty("error").GetString().Should().Contain("cannot access");
        (await RequestAsync(HttpMethod.Put, "/spark/boards/home", ann.Id, new
        {
            tiles = new[] { new { id = "u-1", kind = "chart", target = new Dictionary<string, string>() } },
        })).Status.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_catalog_lists_only_what_the_user_may_open()
    {
        var ann = await SeedUserAsync("ann");

        var (status, catalog) = await RequestAsync(HttpMethod.Get, "/spark/boards/home/catalog", ann.Id);

        status.Should().Be(HttpStatusCode.OK);
        var queries = catalog.GetProperty("entries").EnumerateArray()
            .Where(e => e.GetProperty("group").GetString() == "queries")
            .Select(e => e.GetProperty("target").GetProperty("query").GetString())
            .ToList();
        queries.Should().Contain(NotesQueryId.ToString());
        catalog.GetProperty("kinds").EnumerateArray().Select(k => k.GetString()).Should().Contain("count");
    }

    [Fact]
    public void A_board_with_an_unknown_kind_refuses_startup()
    {
        var board = new SparkBoardDefinition { Id = "x", Tiles = [new SparkBoardTile { Id = "t", Kind = "chart" }] };
        ExtraSpark = spark => spark.AddBoards(o => o.Boards.Add(board));

        var act = () => Factory.CreateClient();

        act.Should().Throw<InvalidOperationException>().WithMessage("*kind 'chart'*");
    }
}
