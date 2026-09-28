using System.Net;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Moderation;
using MintPlayer.Spark.Moderation.Documents;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Moderation;

/// <summary>
/// #460 §3.12 moderator tools: locks on every write path (spike S-MOD-D), flags and the review queue,
/// suspensions' server-side write block, the new-account throttle (429), and a moderator's delete
/// reversing the votes of the post.
/// </summary>
public class ModerationToolsTests : SparkTestDriver
{
    private const string Alice = "users/alice", Bob = "users/bob", Carol = "users/carol";

    private async Task<MoHost> StartAsync(Action<SparkModerationOptions>? configure = null)
    {
        var host = await MoHost.StartAsync(Store, configure);
        foreach (var user in new[] { Alice, Bob, Carol, "users/mod" })
            await host.SeedUserAsync(user);
        return host;
    }

    private static async Task LockAsync(MoHost host, string post)
        => (await host.ModeratorAsync("/spark/moderation/lock", Wire.Typed(MoHost.PostTypeId, new { reason = "heated" }, post)))
            .Status.Should().Be(HttpStatusCode.OK);

    private static void ShouldBeLocked((HttpStatusCode Status, System.Text.Json.JsonElement Body) response, string path)
    {
        response.Status.Should().Be(HttpStatusCode.BadRequest, path);
        response.Body.GetRawText().Should().Contain("This post is locked.", path);
    }

    // ---- S-MOD-D: one falsifiable test per write path ----------------------------------------------

    [Fact]
    public async Task S_MOD_D_a_lock_refuses_every_write_path_for_non_moderators()
    {
        await using var host = await StartAsync();
        var post = await host.SeedPostAsync(Alice, "original");
        // A revision to revert to, made before the lock by the author.
        (await host.SendAsync("/spark/po/update", MoHost.UpdateBody(post, ("Title", "second")), Alice)).Status.Should().Be(HttpStatusCode.OK);
        var (_, revisions) = await host.SendAsync("/spark/po/revisions", Wire.Typed(MoHost.PostTypeId, id: post), Alice);
        var oldest = revisions.GetProperty("result").EnumerateArray().Last().GetProperty("changeVector").GetString();
        await LockAsync(host, post);

        // PO save.
        ShouldBeLocked(await host.SendAsync("/spark/po/update", MoHost.UpdateBody(post, ("Title", "edited")), Alice), "update");
        // AsDetail parent: adding / removing a row is a save of the parent.
        ShouldBeLocked(await host.SendAsync("/spark/po/update", MoHost.UpdateBody(post, ("Lines", new[] { new { Text = "row" } })), Alice), "AsDetail parent");
        // Custom action saving through IDatabaseAccess.
        ShouldBeLocked(await host.SendAsync("/spark/actions/execute", Wire.Action(MoHost.PostTypeId, "MoTouch", new
        {
            selectedItemIds = new[] { post },
            queryId = MoHost.PostsQueryId.ToString(),
        }), Alice), "custom action");
        // Revert (History).
        ShouldBeLocked(await host.SendAsync("/spark/po/revert", Wire.Typed(MoHost.PostTypeId, new { changeVector = oldest }, post), Alice), "revert");
        // Delete.
        ShouldBeLocked(await host.SendAsync("/spark/po/delete", Wire.Typed(MoHost.PostTypeId, id: post), Alice), "delete");

        // Restore and purge (SoftDelete): the moderator deletes (exempt), the author may not undo or purge.
        (await host.ModeratorAsync("/spark/po/delete", Wire.Typed(MoHost.PostTypeId, id: post))).Status.Should().Be(HttpStatusCode.NoContent);
        ShouldBeLocked(await host.SendAsync("/spark/po/restore", Wire.Typed(MoHost.PostTypeId, id: post), Alice), "restore");
        ShouldBeLocked(await host.SendAsync("/spark/po/purge", Wire.Typed(MoHost.PostTypeId, id: post), Alice), "purge");
        (await host.ModeratorAsync("/spark/po/restore", Wire.Typed(MoHost.PostTypeId, id: post))).Status.Should().Be(HttpStatusCode.OK, "moderators are exempt");

        var stored = await host.LoadAsync<MoPost>(post);
        stored!.Title.Should().Be("second");
        stored.Lines.Should().BeEmpty();
        stored.IsDeleted.Should().BeFalse();

        // DeleteRow (/spark/po/delete-row) writes nothing — it is a consultation; the removal reaches the
        // database only as the parent save, which the AsDetail-parent case above refuses.

        // Voting on a locked post is refused too.
        ShouldBeLocked(await host.VoteAsync(Bob, post, 1), "vote");

        // Sync: the owner module's write passes (it already decided) — measured, not refused.
        using (var scope = host.Factory.CreateScope())
        {
            // As the author (no Lock right): the same caller whose Save was refused above.
            scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>().HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                RequestServices = scope.ServiceProvider,
                User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                    [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, Alice)], "MoTest")),
            };
            var access = scope.ServiceProvider.GetRequiredService<IDatabaseAccess>();
            var po = await access.GetPersistentObjectAsync(MoHost.PostTypeId, post);
            po!["Title"].SetValue("synced");
            await access.SavePersistentObjectAsync(po, PersistentObjectOperation.Sync);
        }
        (await host.LoadAsync<MoPost>(post))!.Title.Should().Be("synced");

        // Unlocked: the author edits again.
        (await host.ModeratorAsync("/spark/moderation/unlock", Wire.Typed(MoHost.PostTypeId, id: post))).Status.Should().Be(HttpStatusCode.OK);
        (await host.SendAsync("/spark/po/update", MoHost.UpdateBody(post, ("Title", "after")), Alice)).Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_locked_post_loads_without_Edit_and_Delete_for_those_the_lock_binds()
    {
        await using var host = await StartAsync();
        var post = await host.SeedPostAsync(Alice);
        await LockAsync(host, post);

        var (_, forAuthor) = await host.SendAsync("/spark/po/load", Wire.Typed(MoHost.PostTypeId, id: post), Alice);
        var (_, forModerator) = await host.ModeratorAsync("/spark/po/load", Wire.Typed(MoHost.PostTypeId, id: post));

        forAuthor.GetProperty("disabledActions").EnumerateArray().Select(a => a.GetString()!).Where(a => a is "Edit" or "Delete").Should().HaveCount(2);
        forModerator.TryGetProperty("disabledActions", out var none).Should().BeFalse(none.ToString());
    }

    [Fact]
    public async Task Locking_needs_the_Lock_right_and_is_audited()
    {
        await using var host = await StartAsync();
        var post = await host.SeedPostAsync(Alice);

        (await host.SendAsync("/spark/moderation/lock", Wire.Typed(MoHost.PostTypeId, id: post), Bob)).Status.Should().Be(HttpStatusCode.NotFound);
        await LockAsync(host, post);

        var (_, audit) = await host.ModeratorAsync("/spark/moderation/audit", new { take = 10 });
        audit.GetProperty("result").EnumerateArray().Should().Contain(e =>
            e.GetProperty("action").GetString() == "lock" && e.GetProperty("targetId").GetString() == post && e.GetProperty("actorId").GetString() == "users/mod");
        (await host.SendAsync("/spark/moderation/audit", new { take = 10 }, Bob)).Status.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- flags and the review queue --------------------------------------------------------------

    [Fact]
    public async Task Flags_open_one_case_per_post_and_upholding_credits_every_flagger()
    {
        await using var host = await StartAsync();
        var post = await host.SeedPostAsync(Alice);
        var other = await host.SeedPostAsync(Alice, "other");

        (await host.SendAsync("/spark/moderation/flag", Wire.Typed(MoHost.PostTypeId, new { reason = "spam" }, post), Bob)).Status.Should().Be(HttpStatusCode.OK);
        await host.SendAsync("/spark/moderation/flag", Wire.Typed(MoHost.PostTypeId, new { reason = "spam again" }, post), Bob);
        await host.SendAsync("/spark/moderation/flag", Wire.Typed(MoHost.PostTypeId, new { reason = "rude" }, post), Carol);
        await host.SendAsync("/spark/moderation/flag", Wire.Typed(MoHost.PostTypeId, new { reason = "meh" }, other), Carol);
        (await host.SendAsync("/spark/moderation/flag", Wire.Typed(MoHost.PostTypeId, new { reason = "" }, post), Carol)).Status.Should().Be(HttpStatusCode.BadRequest);

        var caseId = ModerationIds.FlagCase(post);
        (await host.LoadAsync<ReviewCase>(caseId))!.FlagCount.Should().Be(2, "one open flag per user and post");

        (await host.SendAsync("/spark/moderation/cases", new { }, Bob)).Status.Should().Be(HttpStatusCode.NotFound, "no Review right");
        var (_, queue) = await host.ModeratorAsync("/spark/moderation/cases", new { });
        queue.GetProperty("result").EnumerateArray().Select(c => c.GetProperty("id").GetString()).Should().BeEquivalentTo([caseId, ModerationIds.FlagCase(other)]);

        (await host.ModeratorAsync("/spark/moderation/case/decide", new { caseId, decision = "uphold" })).Status.Should().Be(HttpStatusCode.OK);
        (await host.ModeratorAsync("/spark/moderation/case/decide", new { caseId = ModerationIds.FlagCase(other), decision = "decline" })).Status.Should().Be(HttpStatusCode.OK);

        (await host.SummaryAsync(Bob)).Total.Should().Be(2, "flag upheld: +2, credited at once");
        (await host.SummaryAsync(Carol)).Total.Should().Be(2, "one upheld (+2), one declined (0)");
        (await host.LoadAsync<ModerationFlag>(ModerationIds.Flag(Carol, other)))!.Status.Should().Be(ModerationCaseStatus.Declined);
    }

    // ---- suspensions -----------------------------------------------------------------------------

    [Fact]
    public async Task A_suspension_blocks_writes_votes_and_privileges_on_the_next_request()
    {
        await using var host = await StartAsync();
        var post = await host.SeedPostAsync(Alice);
        var bobsPost = await host.SeedPostAsync(Bob, "bob's");
        (await host.VoteAsync(Bob, post, 1)).Status.Should().Be(HttpStatusCode.OK);

        (await host.SendAsync("/spark/moderation/suspend", new { userId = Bob, days = 3 }, Carol)).Status.Should().Be(HttpStatusCode.NotFound, "Suspend/Moderation only");
        (await host.ModeratorAsync("/spark/moderation/suspend", new { userId = Bob, days = 3, reason = "ring" })).Status.Should().Be(HttpStatusCode.OK);

        var update = await host.SendAsync("/spark/po/update", MoHost.UpdateBody(bobsPost, ("Title", "x")), Bob);
        update.Status.Should().Be(HttpStatusCode.BadRequest);
        update.Body.GetRawText().Should().Contain("suspended");
        (await host.VoteAsync(Bob, post, -1)).Status.Should().Be(HttpStatusCode.NotFound, "no reputation groups while suspended: the Downvote privilege is gone");
        var (_, reputation) = await host.SendAsync("/spark/moderation/reputation", new { }, Bob);
        reputation.GetProperty("result").GetProperty("suspended").GetBoolean().Should().BeTrue();
        reputation.GetProperty("result").GetProperty("privileges").GetArrayLength().Should().Be(0);

        // A timed suspension ends by itself.
        host.Clock.Advance(TimeSpan.FromDays(3) + TimeSpan.FromMinutes(1));
        (await host.SendAsync("/spark/po/update", MoHost.UpdateBody(bobsPost, ("Title", "x")), Bob)).Status.Should().Be(HttpStatusCode.OK);
    }

    // ---- new-account throttle --------------------------------------------------------------------

    [Fact]
    public async Task A_new_account_posting_too_often_is_throttled_with_429()
    {
        await using var host = await StartAsync(o =>
        {
            o.NewAccounts.AccountAgeDays = 7;
            o.NewAccounts.MaxPostsPerDay = 2;
        });
        await host.SeedUserAsync("users/fresh", ageDays: 1);

        (await host.SendAsync("/spark/po/create", MoHost.CreateBody(("Title", "1")), "users/fresh")).Status.Should().Be(HttpStatusCode.Created);
        (await host.SendAsync("/spark/po/create", MoHost.CreateBody(("Title", "2")), "users/fresh")).Status.Should().Be(HttpStatusCode.Created);
        var (third, body) = await host.SendAsync("/spark/po/create", MoHost.CreateBody(("Title", "3")), "users/fresh");
        for (var i = 0; i < 3; i++)
            (await host.SendAsync("/spark/po/create", MoHost.CreateBody(("Title", $"old-{i}")), Alice)).Status.Should().Be(HttpStatusCode.Created, "an old account is not throttled");

        third.Should().Be(HttpStatusCode.TooManyRequests, "429, not 404 or 400");
        body.GetProperty("result").GetProperty("retryAfterSeconds").GetInt32().Should().Be(12 * 3600);
        host.Clock.Advance(TimeSpan.FromDays(1));
        (await host.SendAsync("/spark/po/create", MoHost.CreateBody(("Title", "3")), "users/fresh")).Status.Should().Be(HttpStatusCode.Created);
    }

    // ---- a moderator's delete reverses the post's votes -------------------------------------------

    [Fact]
    public async Task A_moderator_deleting_someone_elses_post_reverses_its_votes_and_the_author_deleting_does_not()
    {
        await using var host = await StartAsync(o => o.Fraud.CreditDelayHours = 0);
        var removed = await host.SeedPostAsync(Alice, "removed");
        var withdrawn = await host.SeedPostAsync(Alice, "withdrawn");
        await host.VoteAsync(Bob, removed, 1);
        await host.VoteAsync(Bob, withdrawn, 1);

        (await host.ModeratorAsync("/spark/po/delete", Wire.Typed(MoHost.PostTypeId, id: removed))).Status.Should().Be(HttpStatusCode.NoContent);
        (await host.SendAsync("/spark/po/delete", Wire.Typed(MoHost.PostTypeId, id: withdrawn), Alice)).Status.Should().Be(HttpStatusCode.NoContent);

        var alice = await host.EventsForAsync(Alice);
        alice.Should().ContainSingle(e => e.Kind == ReputationEventKinds.Reversal).Which.RuleId.Should().Be("content-deleted");
        alice.Single(e => e.Kind == ReputationEventKinds.Reversal).TargetId.Should().Be(removed);
        await host.CreditAsync();
        (await host.SummaryAsync(Alice)).Total.Should().Be(10, "the author's own delete keeps what the post earned");
    }
}
