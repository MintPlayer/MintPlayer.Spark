using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Live;
using MintPlayer.Spark.Organizations;
using MintPlayer.Spark.Organizations.Models;
using MintPlayer.Spark.Tests.Organizations;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Live;

/// <summary>
/// The live socket (X4) through the real route table, the organizations row policy and the expiration filter
/// (<see cref="OgNote"/> is organization-owned and expiring, its type and query are <c>"live": true</c>).
/// </summary>
/// <remarks>
/// Each test states what it proves; the ones that prove a mechanism name what fails without it (PRD §12
/// "Spike S8" records the red runs).
/// </remarks>
public class LiveSocketTests : OrganizationsTestHost
{
    private static readonly Uri Server = new("http://localhost/");

    private async Task<LiveTestClient> ConnectAsync(string? userId)
        => await LiveTestClient.ConnectAsync(Factory.CreateWebSocketClient(), Server, request =>
        {
            if (userId is not null)
                request.Headers[OgPrincipalStartupFilter.Header] = userId;
        });

    private async Task<(SparkUser Ann, Organization Org, OgNote Note)> SeedAsync()
    {
        var ann = await SeedUserAsync("ann");
        var org = await SeedOrganizationAsync("Acme", ann);
        var note = await SeedNoteAsync("v1", org.Id);
        return (ann, org, note);
    }

    private async Task WriteAsync(string id, Action<OgNote> change, IDocumentStore? store = null)
    {
        using var session = (store ?? Store).OpenAsyncSession();
        var note = await session.LoadAsync<OgNote>(id);
        change(note);
        await session.SaveChangesAsync();
    }

    /// <summary>A second client of the same database: its writes raise no event in the host's store (another process).</summary>
    private IDocumentStore OtherProcess()
    {
        var other = new DocumentStore { Urls = Store.Urls, Database = Store.Database, Certificate = Store.Certificate };
        other.Initialize();
        return other;
    }

    private static string Title(JsonElement po)
        => po.GetProperty("attributes").EnumerateArray().First(a => a.GetProperty("name").GetString() == "Title").GetProperty("value").GetString()!;

    private static object NotesQuery(int take = 50) => new { queryId = NotesQueryId.ToString(), take };

    // ---- the change sources (D33) ---------------------------------------------------------------------

    [Theory]
    [InlineData(SparkLiveSource.Both, true)]
    [InlineData(SparkLiveSource.Changes, true)]
    [InlineData(SparkLiveSource.Actions, false)]
    public async Task A_write_from_another_process_reaches_the_viewer_only_through_the_Changes_API(SparkLiveSource source, bool seen)
    {
        LiveOptions = o => o.Source = source;
        var (ann, _, note) = await SeedAsync();
        await using var client = await ConnectAsync(ann.Id);
        await client.SubscribePoAsync("n", NoteTypeId.ToString(), note.Id!);
        Title((await client.NextAsync("po", "n")).GetProperty("po")).Should().Be("v1");

        using var other = OtherProcess();
        await WriteAsync(note.Id!, n => n.Title = "v2", other);

        var frame = await client.TryNextAsync(TimeSpan.FromSeconds(seen ? 10 : 3));
        if (!seen)
        {
            frame.Should().BeNull("under Source = Actions nothing watches the database: another process's write is invisible until Revalidate");
            return;
        }
        frame!.Value.GetProperty("type").GetString().Should().Be("poPatch");
        frame.Value.GetProperty("patch").GetProperty("attributes").EnumerateArray()
            .Single().GetProperty("value").GetString().Should().Be("v2", "only the changed attribute travels");
    }

    [Fact]
    public async Task A_write_in_this_process_reaches_the_viewer_under_Source_Actions_without_the_Changes_API()
    {
        // The store-level events (LiveActionsSource): a raw-session write, not even through the pipeline.
        LiveOptions = o => o.Source = SparkLiveSource.Actions;
        var (ann, _, note) = await SeedAsync();
        await using var client = await ConnectAsync(ann.Id);
        await client.SubscribePoAsync("n", NoteTypeId.ToString(), note.Id!);
        await client.NextAsync("po", "n");

        await WriteAsync(note.Id!, n => n.Title = "v2");

        var patch = await client.NextAsync("poPatch", "n", TimeSpan.FromSeconds(5));
        patch.GetProperty("patch").GetProperty("attributes")[0].GetProperty("value").GetString().Should().Be("v2");
    }

    [Fact]
    public async Task Under_Both_one_write_costs_one_evaluation_not_one_per_source()
    {
        // The store event and the Changes notification of a write carry the same id and change vector; the
        // second is dropped (LiveHub de-duplication), so the viewer is evaluated once, not twice.
        var (ann, _, note) = await SeedAsync();
        await using var client = await ConnectAsync(ann.Id);
        await client.SubscribePoAsync("n", NoteTypeId.ToString(), note.Id!);
        await client.NextAsync("po", "n");
        var hub = Factory.GetService<LiveHub>();
        var before = hub.Counters;

        await WriteAsync(note.Id!, n => n.Title = "v2");
        await client.NextAsync("poPatch", "n");
        await Task.Delay(1000); // the Changes echo arrives 4–22 ms after the commit; give it ample time

        var after = hub.Counters;
        (after.Deduplicated - before.Deduplicated).Should().Be(1, "the Changes echo of the in-process write is dropped");
        (await client.TryNextAsync(TimeSpan.FromMilliseconds(700))).Should().BeNull();
    }

    // ---- one read path ----------------------------------------------------------------------------------

    [Fact]
    public async Task The_pushed_object_and_page_are_exactly_what_load_and_execute_return()
    {
        var (ann, _, note) = await SeedAsync();
        await SeedNoteAsync("second", (await LoadAsync<OgNote>(note.Id!))!.OrganizationId);
        await using var client = await ConnectAsync(ann.Id);

        await client.SubscribePoAsync("n", NoteTypeId.ToString(), note.Id!);
        var pushed = (await client.NextAsync("po", "n")).GetProperty("po");
        var (status, loaded) = await SendAsync("/spark/po/load", new { objectTypeId = NoteTypeId.ToString(), id = note.Id }, ann.Id);
        status.Should().Be(HttpStatusCode.OK);
        JsonNode.DeepEquals(JsonNode.Parse(pushed.GetRawText()), JsonNode.Parse(loaded.GetRawText())).Should().BeTrue(
            "a push re-runs /spark/po/load: row security, redaction, attribute rights and disabled actions alike");

        await client.SubscribeQueryAsync("q", NotesQuery());
        var page = (await client.NextAsync("result", "q")).GetProperty("result");
        var (executeStatus, executed) = await SendAsync("/spark/queries/execute", NotesQuery(), ann.Id);
        executeStatus.Should().Be(HttpStatusCode.OK);
        JsonNode.DeepEquals(JsonNode.Parse(page.GetRawText()), JsonNode.Parse(executed.GetRawText())).Should().BeTrue();
        page.GetProperty("totalItems").GetInt32().Should().Be(2);
    }

    // ---- no leak, no oracle (#453) -------------------------------------------------------------------------

    [Fact]
    public async Task A_write_to_a_row_the_viewer_cannot_see_sends_nothing()
    {
        var (ann, org, note) = await SeedAsync();
        var carl = await SeedUserAsync("carl");
        var carlsOrg = await SeedOrganizationAsync("Initech", carl);
        var hidden = await SeedNoteAsync("hidden", carlsOrg.Id);
        await using var client = await ConnectAsync(ann.Id);
        await client.SubscribeQueryAsync("q", NotesQuery());
        (await client.NextAsync("result", "q")).GetProperty("result").GetProperty("totalItems").GetInt32().Should().Be(1);

        // Same collection, other organization: re-evaluated, diffed, and nothing visible changed.
        await WriteAsync(hidden.Id!, n => n.Title = "hidden 2");
        await SeedNoteAsync("hidden new", carlsOrg.Id);
        (await client.TryNextAsync(TimeSpan.FromSeconds(2))).Should().BeNull("a hidden row's change, or its existence, must not be observable");

        // The socket was watching all along: a visible write arrives.
        await WriteAsync(note.Id!, n => n.Title = "v2");
        var patch = await client.NextAsync("patch", "q");
        patch.GetProperty("patch").GetProperty("upsert").GetArrayLength().Should().Be(1);
        _ = org;
    }

    [Fact]
    public async Task A_member_removed_from_the_organization_gets_gone_without_any_write_to_the_note()
    {
        LiveOptions = o => o.Revalidate = TimeSpan.FromSeconds(2);
        var (ann, org, note) = await SeedAsync();
        var bob = await SeedUserAsync("bob");
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new OrganizationMembership
            {
                Id = OrganizationMembership.IdFor(bob.Id!, org.Id!),
                OrganizationId = org.Id,
                UserId = bob.Id,
                UserName = bob.UserName,
                Role = OrganizationRole.Viewer,
                JoinedAtUtc = DateTime.UtcNow,
            });
            await session.SaveChangesAsync();
        }

        await using var client = await ConnectAsync(bob.Id);
        await client.SubscribePoAsync("n", NoteTypeId.ToString(), note.Id!);
        await client.NextAsync("po", "n");
        // Let the subscription settle (the evaluation that follows the Changes connection coming up), so
        // nothing but Revalidate is left to notice the membership going.
        (await client.TryNextAsync(TimeSpan.FromSeconds(1.5))).Should().BeNull();

        using (var session = Store.OpenAsyncSession())
        {
            session.Delete(OrganizationMembership.IdFor(bob.Id!, org.Id!));
            await session.SaveChangesAsync();
        }

        var gone = await client.NextAsync(TimeSpan.FromSeconds(5));
        gone.GetProperty("type").GetString().Should().Be("gone", "the membership is re-read on every evaluation, and Revalidate evaluates without a write");
        gone.GetProperty("sub").GetString().Should().Be("n");
        (await SendAsync("/spark/po/load", new { objectTypeId = NoteTypeId.ToString(), id = note.Id }, bob.Id)).Status.Should().Be(HttpStatusCode.NotFound);
        _ = ann;
    }

    [Fact]
    public async Task An_expired_note_is_gone_like_a_deleted_one()
    {
        var (ann, _, note) = await SeedAsync();
        var deleted = await SeedNoteAsync("to delete", note.OrganizationId);
        await using var client = await ConnectAsync(ann.Id);
        await client.SubscribePoAsync("expired", NoteTypeId.ToString(), note.Id!);
        await client.SubscribePoAsync("deleted", NoteTypeId.ToString(), deleted.Id!);
        await client.NextAsync("po", "expired");
        await client.NextAsync("po", "deleted");

        await WriteAsync(note.Id!, n => n.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1));
        using (var session = Store.OpenAsyncSession())
        {
            session.Delete(deleted.Id!);
            await session.SaveChangesAsync();
        }

        var first = await client.NextAsync();
        var second = await client.NextAsync();
        new[] { first, second }.Select(f => f.GetProperty("type").GetString()).Should().Equal("gone", "gone");
        new[] { first, second }.Select(f => f.GetProperty("sub").GetString()).OrderBy(s => s).Should().Equal("deleted", "expired");
        first.EnumerateObject().Select(p => p.Name).OrderBy(n => n).Should().Equal(second.EnumerateObject().Select(p => p.Name).OrderBy(n => n),
            "expired, deleted and forbidden are one answer");
    }

    [Fact]
    public async Task Unknown_forbidden_and_not_live_answer_alike()
    {
        var (ann, _, _) = await SeedAsync();
        var carl = await SeedUserAsync("carl");
        var carlsNote = await SeedNoteAsync("carl's", (await SeedOrganizationAsync("Initech", carl)).Id);
        await using var client = await ConnectAsync(ann.Id);

        // Objects of a live type: a missing id and a row of another organization are both gone.
        await client.SubscribePoAsync("missing", NoteTypeId.ToString(), "OgNotes/does-not-exist");
        await client.SubscribePoAsync("foreign", NoteTypeId.ToString(), carlsNote.Id!);
        // Queries: an unknown id and an existing query that is not live are both unavailable, and so is an
        // object of a type that is not live.
        await client.SubscribeQueryAsync("unknown", new { queryId = Guid.NewGuid().ToString() });
        await client.SubscribeQueryAsync("notlive", new { queryId = OrganizationsQueryId });
        await client.SubscribePoAsync("type", OrganizationTypeId, "anything");

        var frames = new Dictionary<string, JsonElement>();
        for (var i = 0; i < 5; i++)
        {
            var frame = await client.NextAsync();
            frames[frame.GetProperty("sub").GetString()!] = frame;
        }

        frames["missing"].GetProperty("type").GetString().Should().Be("gone");
        frames["foreign"].GetProperty("type").GetString().Should().Be("gone");
        foreach (var sub in new[] { "unknown", "notlive", "type" })
        {
            frames[sub].GetProperty("type").GetString().Should().Be("error");
            frames[sub].GetProperty("code").GetString().Should().Be(LiveCodes.Unavailable);
            frames[sub].GetProperty("message").GetString().Should().Be(LiveCodes.UnavailableMessage);
        }
    }

    // ---- patches ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_new_row_is_added_and_a_deleted_row_removed_from_the_viewers_page()
    {
        var (ann, org, note) = await SeedAsync();
        await SeedNoteAsync("b", org.Id);
        await SeedNoteAsync("c", org.Id);
        await using var client = await ConnectAsync(ann.Id);
        await client.SubscribeQueryAsync("q", NotesQuery());
        (await client.NextAsync("result", "q")).GetProperty("result").GetProperty("totalItems").GetInt32().Should().Be(3);

        var added = await SeedNoteAsync("d", org.Id);
        var patch = (await client.NextAsync("patch", "q")).GetProperty("patch");
        patch.GetProperty("upsert").EnumerateArray().Select(i => i.GetProperty("id").GetString()).Should().Equal(added.Id);
        patch.GetProperty("order").EnumerateArray().Select(i => i.GetString()).Should().Contain(added.Id);
        patch.GetProperty("set").GetProperty("totalItems").GetInt32().Should().Be(4);

        using (var session = Store.OpenAsyncSession())
        {
            session.Delete(note.Id!);
            await session.SaveChangesAsync();
        }
        var removal = (await client.NextAsync("patch", "q")).GetProperty("patch");
        removal.GetProperty("remove").EnumerateArray().Select(i => i.GetString()).Should().Equal(note.Id);
        removal.GetProperty("set").GetProperty("totalItems").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task An_update_changes_the_page_the_subscription_follows()
    {
        var (ann, org, _) = await SeedAsync();
        await SeedNoteAsync("b", org.Id);
        await SeedNoteAsync("c", org.Id);
        await using var client = await ConnectAsync(ann.Id);
        await client.SubscribeQueryAsync("q", new { queryId = NotesQueryId.ToString(), take = 2, sortColumns = new[] { new { property = "Title", direction = "asc" } } });
        var first = (await client.NextAsync("result", "q")).GetProperty("result");
        first.GetProperty("items").GetArrayLength().Should().Be(2);

        await client.UpdateAsync("q", new { queryId = NotesQueryId.ToString(), skip = 2, take = 2, sortColumns = new[] { new { property = "Title", direction = "asc" } } });
        var frame = await client.NextAsync();
        frame.GetProperty("sub").GetString().Should().Be("q");
        // Page 2 of 3 rows: one row; a patch or a full result, whichever is smaller.
        var json = JsonNode.Parse(frame.GetRawText())!;
        (json["type"]!.GetValue<string>() == "result"
            ? json["result"]!["skip"]!.GetValue<int>()
            : json["patch"]!["set"]!["skip"]!.GetValue<int>()).Should().Be(2);
    }

    [Fact]
    public async Task A_malformed_query_request_is_invalid_and_says_why()
    {
        var (ann, _, _) = await SeedAsync();
        await using var client = await ConnectAsync(ann.Id);

        await client.SubscribeQueryAsync("q", new { queryId = NotesQueryId.ToString(), sortColumns = new[] { new { property = "NoSuchColumn", direction = "asc" } } });

        var error = await client.NextAsync("error", "q");
        error.GetProperty("code").GetString().Should().Be(LiveCodes.Invalid);
        error.GetProperty("message").GetString().Should().Contain("NoSuchColumn");
    }

    // ---- bounds (D36) -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_burst_of_writes_costs_at_most_one_push_per_throttle_window_and_ends_on_the_latest_state()
    {
        var (ann, _, note) = await SeedAsync();
        await using var client = await ConnectAsync(ann.Id);
        await client.SubscribePoAsync("n", NoteTypeId.ToString(), note.Id!);
        await client.NextAsync("po", "n");

        var started = DateTime.UtcNow;
        for (var i = 1; i <= 30; i++)
            await WriteAsync(note.Id!, n => n.Title = $"burst {i}");
        var burst = DateTime.UtcNow - started;

        var frames = new List<JsonElement>();
        while (await client.TryNextAsync(TimeSpan.FromSeconds(1.5)) is { } frame)
            frames.Add(frame);

        // Leading edge, then at most one per 500 ms while the burst lasts, then one trailing.
        var bound = 2 + (int)Math.Ceiling(burst.TotalMilliseconds / 500);
        frames.Count.Should().BeInRange(1, bound, $"30 writes in {burst.TotalMilliseconds:0} ms");
        frames.Last().GetProperty("patch").GetProperty("attributes")[0].GetProperty("value").GetString().Should().Be("burst 30", "the latest state wins");
    }

    [Fact]
    public async Task A_subscription_over_the_limit_is_refused_and_the_others_keep_working()
    {
        LiveOptions = o => o.MaxSubscriptionsPerSocket = 2;
        var (ann, _, note) = await SeedAsync();
        await using var client = await ConnectAsync(ann.Id);
        await client.SubscribePoAsync("a", NoteTypeId.ToString(), note.Id!);
        await client.SubscribeQueryAsync("b", NotesQuery());
        await client.SubscribeQueryAsync("c", NotesQuery());

        var limit = await client.NextAsync("error", "c");
        limit.GetProperty("code").GetString().Should().Be(LiveCodes.Limit);
        await client.UnsubscribeAsync("b");
        await client.SubscribeQueryAsync("c", NotesQuery());
        await client.NextAsync("result", "c");
    }

    [Fact]
    public async Task A_socket_over_the_per_user_limit_is_closed_with_4429()
    {
        LiveOptions = o => o.MaxSocketsPerUser = 1;
        var (ann, _, _) = await SeedAsync();
        await using var first = await ConnectAsync(ann.Id);
        await first.SubscribeQueryAsync("q", NotesQuery());
        await first.NextAsync("result", "q");

        await using var second = await ConnectAsync(ann.Id);
        (await second.ClosedAsync(TimeSpan.FromSeconds(5))).Should().Be((WebSocketCloseStatus)LiveCodes.CloseTooManySockets);

        await using var other = await ConnectAsync((await SeedUserAsync("bob")).Id);
        other.State.Should().Be(WebSocketState.Open, "the limit is per user");
    }

    [Fact]
    public async Task The_socket_closes_with_4401_at_its_maximum_lifetime()
    {
        LiveOptions = o => o.MaxSocketLifetime = TimeSpan.FromSeconds(1);
        var (ann, _, _) = await SeedAsync();
        await using var client = await ConnectAsync(ann.Id);
        await client.SubscribeQueryAsync("q", NotesQuery());
        await client.NextAsync("result", "q");

        (await client.ClosedAsync(TimeSpan.FromSeconds(10))).Should().Be((WebSocketCloseStatus)LiveCodes.CloseReauthenticate);
    }

    [Fact]
    public async Task A_quiet_socket_gets_a_ping_at_the_heartbeat()
    {
        LiveOptions = o => o.Heartbeat = TimeSpan.FromMilliseconds(300);
        var (ann, _, _) = await SeedAsync();
        await using var client = await ConnectAsync(ann.Id);
        await client.SubscribeQueryAsync("q", NotesQuery());
        await client.NextAsync("result", "q");
        var framesBefore = client.FramesReceived;

        (await client.TryNextAsync(TimeSpan.FromSeconds(1.2))).Should().BeNull("pings are not frames for a subscription");
        (client.FramesReceived - framesBefore).Should().BeGreaterThanOrEqualTo(2, "a ping after every 300 ms of silence");
    }
}
