using System.Net;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests.Moderation;

namespace MintPlayer.Spark.Tests.Endpoints.PersistentObject.Selection;

/// <summary>
/// #467 spike S8 — a Moderation-locked row inside a delete-many batch (PRD D18): the response, that
/// nothing is written, and that the refusal names the locked row by its breadcrumb.
/// </summary>
/// <remarks>Reuses the Moderation fixture (<see cref="MoHost"/>: MoPost is moderatable and soft-deletable).</remarks>
public class Issue467ModerationBatchTests : SparkTestDriver
{
    private const string Alice = "users/alice";

    // S8 / D18: one locked row refuses the whole batch, nothing is written (neither row soft-deleted),
    // and the message lists the locked row's breadcrumb ({Title}) so the user knows what to untick.
    [Fact]
    public async Task S8_a_locked_row_in_a_delete_many_batch_refuses_the_lot_and_the_message_names_it()
    {
        await using var host = await MoHost.StartAsync(Store);
        foreach (var user in new[] { Alice, "users/mod" })
            await host.SeedUserAsync(user);

        var free = await host.SeedPostAsync(Alice, "free-post-467");
        var locked = await host.SeedPostAsync(Alice, "locked-post-467");
        (await host.ModeratorAsync("/spark/moderation/lock", Wire.Typed(MoHost.PostTypeId, new { reason = "heated" }, locked)))
            .Status.Should().Be(HttpStatusCode.OK);

        // As the list shows them after the lock (D14): each row at its stored version.
        object[] items;
        using (var session = Store.OpenAsyncSession())
        {
            var rows = await session.LoadAsync<MoPost>([free, locked]);
            items = [.. new[] { free, locked }.Select(id => (object)new { id, etag = session.Advanced.GetChangeVectorFor(rows[id]) })];
        }

        var (status, body) = await host.SendAsync("/spark/po/delete-many", Wire.Typed(MoHost.PostTypeId, new
        {
            items,
            queryId = MoHost.PostsQueryId.ToString(),
        }), Alice);
        var text = body.ValueKind == System.Text.Json.JsonValueKind.Undefined ? string.Empty : body.GetRawText();

        (await host.LoadAsync<MoPost>(free))!.IsDeleted.Should().BeFalse("all or nothing: the free row is not deleted either");
        (await host.LoadAsync<MoPost>(locked))!.IsDeleted.Should().BeFalse("the locked row is not deleted");
        status.Should().Be(HttpStatusCode.BadRequest, $"observed body: {text}");
        text.Should().Contain("locked-post-467", "D18: the refusal names every failed row by its breadcrumb");
        text.Should().NotContain("free-post-467", "D18: only the rows that failed are listed");
    }
}
