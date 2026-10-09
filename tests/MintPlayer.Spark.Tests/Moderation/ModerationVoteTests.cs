using System.Net;
using System.Text.Json;
using MintPlayer.Spark.Moderation;
using MintPlayer.Spark.Moderation.Documents;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Moderation;

/// <summary>
/// #460 §3.12 votes and the write-path fraud measures: deterministic vote ids, no double votes, a
/// hidden post indistinguishable from a missing one, voter eligibility (3), caps (4), delayed crediting
/// (5), diversity (2) and the privilege gates (1). Time is the host's clock, never the wall clock.
/// </summary>
public class ModerationVoteTests : SparkTestDriver
{
    private const string Alice = "users/alice", Bob = "users/bob", Carol = "users/carol", Dave = "users/dave", Erin = "users/erin";

    private async Task<MoHost> StartAsync(Action<SparkModerationOptions>? configure = null)
    {
        var host = await MoHost.StartAsync(Store, configure);
        foreach (var user in new[] { Alice, Bob, Carol, Dave, Erin })
            await host.SeedUserAsync(user);
        return host;
    }

    [Fact]
    public async Task A_vote_has_one_deterministic_document_and_voting_twice_changes_nothing()
    {
        await using var host = await StartAsync();
        var post = await host.SeedPostAsync(Alice);

        var (first, body) = await host.VoteAsync(Bob, post, 1);
        var (second, again) = await host.VoteAsync(Bob, post, 1);

        first.Should().Be(HttpStatusCode.OK);
        body.GetProperty("result").GetProperty("score").GetInt32().Should().Be(1);
        second.Should().Be(HttpStatusCode.OK);
        again.GetProperty("result").GetProperty("score").GetInt32().Should().Be(1, "the same vote again is a no-op");
        var vote = await host.LoadAsync<ModerationVote>(ModerationIds.Vote(Bob, post));
        vote!.Direction.Should().Be(1);
        (await host.EventsForAsync(Alice)).Should().ContainSingle().Which.Points.Should().Be(10);
    }

    [Fact]
    public async Task Changing_a_vote_compensates_the_old_entry_and_withdrawing_nets_to_zero()
    {
        await using var host = await StartAsync();
        var post = await host.SeedPostAsync(Alice);

        await host.VoteAsync(Bob, post, 1);
        var (_, down) = await host.VoteAsync(Bob, post, -1);
        var (_, none) = await host.VoteAsync(Bob, post, 0);

        down.GetProperty("result").GetProperty("score").GetInt32().Should().Be(-1);
        none.GetProperty("result").GetProperty("score").GetInt32().Should().Be(0);
        var alice = await host.EventsForAsync(Alice);
        alice.Select(e => e.Kind).Should().BeEquivalentTo([
            ReputationEventKinds.UpvoteReceived, ReputationEventKinds.Retraction,
            ReputationEventKinds.DownvoteReceived, ReputationEventKinds.Retraction]);
        alice.Sum(e => e.Points).Should().Be(0);
        (await host.EventsForAsync(Bob)).Sum(e => e.Points).Should().Be(0, "the down-vote's cost is retracted with it");

        host.Clock.Advance(TimeSpan.FromHours(49));
        await host.CreditAsync();
        (await host.SummaryAsync(Alice)).Total.Should().Be(0);
    }

    [Fact]
    public async Task A_hidden_missing_or_unmoderatable_target_is_one_refusal_and_anonymous_is_refused()
    {
        await using var host = await StartAsync();
        var post = await host.SeedPostAsync(Alice);
        string plainId;
        using (var session = host.Store.OpenAsyncSession())
        {
            var plain = new MoPlain { Title = "plain" };
            await session.StoreAsync(plain);
            await session.SaveChangesAsync();
            plainId = plain.Id!;
        }

        var hidden = await host.SeedPostAsync(Alice, "hidden");
        var (hiddenStatus, hiddenBody) = await host.VoteAsync(Bob, hidden, 1);
        var (missing, missingBody) = await host.VoteAsync(Bob, "MoPosts/does-not-exist", 1);
        hiddenStatus.Should().Be(HttpStatusCode.NotFound);
        hiddenBody.GetRawText().Should().Be(missingBody.GetRawText(), "a hidden post answers exactly like a missing one (#453)");
        (await host.LoadAsync<ModerationVote>(ModerationIds.Vote(Bob, hidden))).Should().BeNull();
        var (plainStatus, plainBody) = await host.SendAsync("/spark/moderation/vote", Wire.Typed(MoHost.PlainTypeId, new { direction = 1 }, plainId), Bob);
        var (anonymous, _) = await host.SendAsync("/spark/moderation/vote", Wire.Typed(MoHost.PostTypeId, new { direction = 1 }, post), user: null);
        var (flagMissing, flagBody) = await host.SendAsync("/spark/moderation/flag", Wire.Typed(MoHost.PostTypeId, new { reason = "spam" }, "MoPosts/does-not-exist"), Bob);

        missing.Should().Be(HttpStatusCode.NotFound);
        plainStatus.Should().Be(HttpStatusCode.NotFound);
        flagMissing.Should().Be(HttpStatusCode.NotFound);
        plainBody.GetRawText().Should().Be(missingBody.GetRawText(), "a type that is not moderatable answers like a row that does not exist");
        flagBody.GetRawText().Should().Be(missingBody.GetRawText());
        anonymous.Should().Be(HttpStatusCode.NotFound, "a host without a sign-in scheme refuses everyone with 404 (SparkDenial)");
    }

    [Fact]
    public async Task The_vote_state_says_which_arrows_the_caller_holds_the_right_for()
    {
        // The widget disables an arrow instead of offering a click the server refuses with 404.
        await using var host = await StartAsync(o =>
            o.Privileges["Downvote"] = new ModerationPrivilegeOptions { GroupId = MoSecurity.Downvoters, Rep = 125, Grants = ["Downvote"] });
        var post = await host.SeedPostAsync(Alice);

        var (status, body) = await host.SendAsync("/spark/moderation/votes", Wire.Typed(MoHost.PostTypeId, new { ids = new[] { post } }), Bob);
        var (voted, votedBody) = await host.VoteAsync(Bob, post, 1);

        status.Should().Be(HttpStatusCode.OK);
        var state = body.GetProperty("result")[0];
        state.GetProperty("canUpvote").GetBoolean().Should().BeTrue("Upvote needs no reputation here");
        state.GetProperty("canDownvote").GetBoolean().Should().BeFalse("Downvote needs 125 reputation");
        voted.Should().Be(HttpStatusCode.OK);
        votedBody.GetProperty("result").GetProperty("canUpvote").GetBoolean().Should().BeTrue("a vote's answer carries the rights too");
        votedBody.GetProperty("result").GetProperty("canDownvote").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Voting_on_your_own_post_is_refused()
    {
        await using var host = await StartAsync();
        var post = await host.SeedPostAsync(Alice);

        var (status, _) = await host.VoteAsync(Alice, post, 1);

        status.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- fraud measure 3: voter eligibility -------------------------------------------------------

    [Fact]
    public async Task M3_a_new_or_low_reputation_voter_changes_the_score_but_gives_no_reputation()
    {
        await using var host = await StartAsync(o =>
        {
            o.Fraud.EligibleVoterMinAgeDays = 7;
            o.Fraud.EligibleVoterMinReputation = 50;
        });
        await host.SeedUserAsync("users/newbie", ageDays: 3);
        await host.SeedUserAsync("users/veteran", ageDays: 30);
        using (var session = host.Store.OpenAsyncSession())
        {
            await session.StoreAsync(new ReputationSummary { UserId = "users/veteran", Total = 60 }, ModerationIds.Summary("users/veteran"));
            await session.StoreAsync(new ReputationSummary { UserId = Carol, Total = 10 }, ModerationIds.Summary(Carol));
            await session.SaveChangesAsync();
        }
        var post = await host.SeedPostAsync(Alice);

        await host.VoteAsync("users/newbie", post, 1);
        await host.VoteAsync(Carol, post, 1);
        var (_, body) = await host.VoteAsync("users/veteran", post, 1);

        body.GetProperty("result").GetProperty("score").GetInt32().Should().Be(3, "every vote moves the score");
        var events = (await host.EventsForAsync(Alice)).ToDictionary(e => e.VoterId!);
        events["users/newbie"].Points.Should().Be(0);
        events["users/newbie"].ZeroedBy.Should().Be("ineligible-voter", "3 days old");
        events[Carol].ZeroedBy.Should().Be("ineligible-voter", "10 reputation");
        events["users/veteran"].Points.Should().Be(10);
    }

    // ---- fraud measure 4: caps -------------------------------------------------------------------

    [Fact]
    public async Task M4_a_voter_casting_more_than_the_daily_limit_is_throttled_with_429()
    {
        await using var host = await StartAsync(o => o.Fraud.MaxVotesCastPerDay = 2);
        var posts = new List<string>();
        for (var i = 0; i < 3; i++)
            posts.Add(await host.SeedPostAsync(Alice, $"p{i}"));

        (await host.VoteAsync(Bob, posts[0], 1)).Status.Should().Be(HttpStatusCode.OK);
        (await host.VoteAsync(Bob, posts[1], 1)).Status.Should().Be(HttpStatusCode.OK);
        var (third, body) = await host.VoteAsync(Bob, posts[2], 1);

        third.Should().Be(HttpStatusCode.TooManyRequests);
        body.GetProperty("result").GetProperty("retryAfterSeconds").GetInt32().Should().Be(12 * 3600, "the clock is at noon UTC");

        host.Clock.Advance(TimeSpan.FromDays(1));
        (await host.VoteAsync(Bob, posts[2], 1)).Status.Should().Be(HttpStatusCode.OK, "a new UTC day, a new allowance");
    }

    [Fact]
    public async Task M4_a_voter_to_author_pair_is_credited_at_most_N_times_a_day_and_M_times_in_30_days()
    {
        await using var host = await StartAsync(o =>
        {
            o.Fraud.MaxCreditedPairVotesPerDay = 2;
            o.Fraud.MaxCreditedPairVotesPer30Days = 3;
        });
        var posts = new List<string>();
        for (var i = 0; i < 5; i++)
            posts.Add(await host.SeedPostAsync(Alice, $"p{i}"));

        await host.VoteAsync(Bob, posts[0], 1);
        await host.VoteAsync(Bob, posts[1], 1);
        await host.VoteAsync(Bob, posts[2], 1); // third today: capped
        host.Clock.Advance(TimeSpan.FromDays(2));
        await host.VoteAsync(Bob, posts[3], 1); // 3rd credited in 30 days: allowed
        await host.VoteAsync(Bob, posts[4], 1); // 4th in 30 days: capped

        var byTarget = (await host.EventsForAsync(Alice)).ToDictionary(e => e.TargetId!);
        byTarget[posts[0]].Points.Should().Be(10);
        byTarget[posts[1]].Points.Should().Be(10);
        byTarget[posts[2]].ZeroedBy.Should().Be("pair-cap");
        byTarget[posts[3]].Points.Should().Be(10);
        byTarget[posts[4]].ZeroedBy.Should().Be("pair-cap");
        (await host.LoadAsync<ModerationTally>(ModerationIds.Tally(posts[4])))!.Score.Should().Be(1, "the score still moves");
    }

    [Fact]
    public async Task M4_a_recipient_earns_at_most_the_daily_cap_from_votes()
    {
        await using var host = await StartAsync(o => o.Fraud.MaxReputationPerRecipientPerDay = 15);
        var post = await host.SeedPostAsync(Alice);

        await host.VoteAsync(Bob, post, 1);
        await host.VoteAsync(Carol, post, 1);
        await host.VoteAsync(Dave, post, 1);

        var points = (await host.EventsForAsync(Alice)).ToDictionary(e => e.VoterId!, e => (e.Points, e.ZeroedBy));
        points[Bob].Should().Be((10, (string?)null));
        points[Carol].Should().Be((5, (string?)null), "clamped to the remaining headroom");
        points[Dave].Should().Be((0, "daily-cap"));
    }

    // ---- fraud measure 5: delayed crediting ------------------------------------------------------

    [Fact]
    public async Task M5_nothing_a_vote_earns_counts_until_the_crediting_job_ran_after_the_delay()
    {
        await using var host = await StartAsync(o => o.Fraud.CreditDelayHours = 48);
        var post = await host.SeedPostAsync(Alice);
        await host.VoteAsync(Bob, post, 1);

        var entry = (await host.EventsForAsync(Alice)).Single();
        entry.Credited.Should().BeFalse();
        entry.CreditableAfterUtc.Should().Be(host.Clock.Now.UtcDateTime.AddHours(48));

        host.Clock.Advance(TimeSpan.FromHours(47));
        (await host.CreditAsync()).Should().Be(0);
        var early = await host.SummaryAsync(Alice);
        early.Total.Should().Be(0);
        early.Pending.Should().Be(10);

        host.Clock.Advance(TimeSpan.FromHours(2));
        (await host.CreditAsync()).Should().Be(1);
        var late = await host.SummaryAsync(Alice);
        late.Total.Should().Be(10);
        late.Pending.Should().Be(0);
        (await host.CreditAsync()).Should().Be(0, "crediting is idempotent");
    }

    [Fact]
    public async Task M5_the_badge_shows_a_new_vote_as_pending_at_once_without_any_recompute()
    {
        // M13 finding: a vote never recomputed its recipient's summary, so "+N pending" stayed stale
        // for the whole 48 h delay. No SummaryAsync / crediting here: only the vote and the badge read.
        //
        // "At once" means without a recompute or the credit delay, not within the badge read's own
        // 2 s index wait: that wait is bounded on purpose (a badge serves a stale figure rather than
        // fail), and under a fully loaded sweep the map-reduce index took longer, so the read
        // returned 0. The test therefore waits for the pending index itself, which the vote's own
        // transaction feeds, before reading the badge. A recompute is still never run.
        await using var host = await StartAsync(o => o.Fraud.CreditDelayHours = 48);
        var post = await host.SeedPostAsync(Alice);

        await host.VoteAsync(Bob, post, 1);
        await host.WaitForPendingReputationIndexAsync();
        var (status, voted) = await host.SendAsync("/spark/moderation/reputation", new { }, Alice);
        status.Should().Be(HttpStatusCode.OK);
        voted.GetProperty("result").GetProperty("pending").GetInt32().Should().Be(10);
        voted.GetProperty("result").GetProperty("total").GetInt32().Should().Be(0);

        await host.VoteAsync(Bob, post, 0);
        await host.WaitForPendingReputationIndexAsync();
        var (_, withdrawn) = await host.SendAsync("/spark/moderation/reputation", new { }, Alice);
        withdrawn.GetProperty("result").GetProperty("pending").GetInt32().Should().Be(0, "the withdrawal nets the pending entry to zero");
    }

    [Fact]
    public async Task The_own_reputation_says_whether_the_caller_may_review_and_someone_elses_never_does()
    {
        // The client shows the review-queue link from this flag; a moderator holds Review by group.
        await using var host = await StartAsync();
        await host.SeedUserAsync("users/mod");

        var (_, moderator) = await host.ModeratorAsync("/spark/moderation/reputation", new { });
        var (_, plain) = await host.SendAsync("/spark/moderation/reputation", new { }, Bob);
        var (_, other) = await host.ModeratorAsync("/spark/moderation/reputation", new { userId = "users/mod" }, user: "users/mod");
        var (_, asked) = await host.SendAsync("/spark/moderation/reputation", new { userId = "users/mod" }, Bob);

        moderator.GetProperty("result").GetProperty("canReview").GetBoolean().Should().BeTrue();
        plain.GetProperty("result").GetProperty("canReview").GetBoolean().Should().BeFalse("Review needs 1000 reputation here");
        other.GetProperty("result").GetProperty("canReview").GetBoolean().Should().BeTrue("naming yourself is still your own reputation");
        asked.GetProperty("result").GetProperty("canReview").GetBoolean().Should().BeFalse("someone else's badge carries the number only");
    }

    [Fact]
    public async Task An_anonymous_caller_gets_no_reputation_and_no_401()
    {
        // The badge sits in every author cell of pages anonymous visitors may read; a 401 sent the
        // whole QnA app to the sign-in page (ng-spark/auth's interceptor). "None" is the honest answer,
        // and it discloses nothing: not the caller's own (there is none), not anyone else's total.
        await using var host = await StartAsync();
        var post = await host.SeedPostAsync(Alice);
        await host.VoteAsync(Bob, post, 1);

        var (ownStatus, own) = await host.SendAsync("/spark/moderation/reputation", new { }, user: null);
        var (otherStatus, other) = await host.SendAsync("/spark/moderation/reputation", new { userId = Alice }, user: null);
        var (historyStatus, _) = await host.SendAsync("/spark/moderation/reputation/history", new { take = 50 }, user: null);

        ownStatus.Should().Be(HttpStatusCode.OK);
        own.GetProperty("result").ValueKind.Should().Be(JsonValueKind.Null);
        otherStatus.Should().Be(HttpStatusCode.OK);
        other.GetProperty("result").ValueKind.Should().Be(JsonValueKind.Null, "an anonymous caller is shown nobody's reputation");
        // The ledger is a page of its own and stays refused (404 here: this host has no way to sign
        // in, so SparkDenial does not promise that authenticating would help).
        historyStatus.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- fraud measure 2: diversity --------------------------------------------------------------

    [Fact]
    public async Task M2_vote_reputation_counts_toward_privileges_only_from_enough_distinct_voters_and_days()
    {
        await using var host = await StartAsync(o =>
        {
            o.Fraud.CreditDelayHours = 0;
            o.Fraud.MaxCreditedPairVotesPerDay = 100;
            o.Fraud.MaxCreditedPairVotesPer30Days = 100;
        });
        var posts = new List<string>();
        for (var i = 0; i < 8; i++)
            posts.Add(await host.SeedPostAsync(Alice, $"p{i}"));

        // Two voters: 2 votes, but fewer than max(3, 2/5) distinct voters.
        await host.VoteAsync(Bob, posts[0], 1);
        await host.VoteAsync(Carol, posts[0], 1);
        await host.CreditAsync();
        var two = await host.SummaryAsync(Alice);
        two.Total.Should().Be(20);
        two.DiversityMet.Should().BeFalse();
        two.PrivilegeReputation.Should().Be(0);

        // A third voter: 3 votes from 3 voters on 1 day (3 / 4 = 0 days needed).
        await host.VoteAsync(Dave, posts[0], 1);
        await host.CreditAsync();
        var three = await host.SummaryAsync(Alice);
        three.DiversityMet.Should().BeTrue();
        three.PrivilegeReputation.Should().Be(30);

        // Bob piles on: 8 votes from 3 voters on 1 day needs 8 / 4 = 2 distinct days.
        for (var i = 1; i < 6; i++)
            await host.VoteAsync(Bob, posts[i], 1);
        await host.CreditAsync();
        var piled = await host.SummaryAsync(Alice);
        piled.CreditedVotes.Should().Be(8);
        piled.DistinctDays.Should().Be(1);
        piled.DiversityMet.Should().BeFalse();
        piled.PrivilegeReputation.Should().Be(0);

        // Another day evens it out.
        host.Clock.Advance(TimeSpan.FromDays(1));
        await host.VoteAsync(Erin, posts[6], 1);
        await host.CreditAsync();
        var spread = await host.SummaryAsync(Alice);
        spread.CreditedVotes.Should().Be(9);
        spread.DistinctDays.Should().Be(2);
        spread.DiversityMet.Should().BeTrue();
        spread.PrivilegeReputation.Should().Be(90);
    }

    // ---- fraud measure 1: age + activity gates on every privilege --------------------------------

    [Fact]
    public async Task M1_a_privilege_needs_its_reputation_account_age_and_active_days()
    {
        await using var host = await StartAsync(o =>
            o.Privileges["Upvote"] = new ModerationPrivilegeOptions { GroupId = MoSecurity.Voters, Rep = 15, MinAccountAgeDays = 10, MinActiveDays = 2 });
        await host.SeedUserAsync("users/young", ageDays: 5);
        using (var session = host.Store.OpenAsyncSession())
        {
            foreach (var user in new[] { "users/young", Bob })
                await session.StoreAsync(new ReputationSummary { UserId = user, Total = 20, PrivilegeReputation = 20 }, ModerationIds.Summary(user));
            await session.StoreAsync(new ReputationSummary { UserId = Carol, Total = 20, PrivilegeReputation = 10 }, ModerationIds.Summary(Carol));
            await session.SaveChangesAsync();
        }
        var post = await host.SeedPostAsync(Alice);

        // Day 1: Bob is active for the first time — 1 active day of the 2 required.
        (await host.VoteAsync(Bob, post, 1)).Status.Should().Be(HttpStatusCode.NotFound, "no Vote right yet: 1 active day");
        host.Clock.Advance(TimeSpan.FromDays(1));
        (await host.VoteAsync(Bob, post, 1)).Status.Should().Be(HttpStatusCode.OK, "rep 20 ≥ 15, 365 days old, 2 active days");

        host.Clock.Advance(TimeSpan.FromDays(1));
        await host.VoteAsync("users/young", post, 1);
        host.Clock.Advance(TimeSpan.FromDays(1));
        (await host.VoteAsync("users/young", post, 1)).Status.Should().Be(HttpStatusCode.NotFound, "7 days old, 10 required");
        await host.VoteAsync(Carol, post, 1);
        host.Clock.Advance(TimeSpan.FromDays(1));
        (await host.VoteAsync(Carol, post, 1)).Status.Should().Be(HttpStatusCode.NotFound, "privilege reputation 10 < 15 (the diversity rule withheld the rest)");

        var (_, reputation) = await host.SendAsync("/spark/moderation/reputation", new { }, Bob);
        reputation.GetProperty("result").GetProperty("privileges").EnumerateArray().Select(p => p.GetString()).Should().Contain("Upvote");
    }

    [Fact]
    public async Task M10_account_age_comes_from_the_SparkUser_CreatedAtUtc_and_unknown_is_treated_as_new()
    {
        await using var host = await StartAsync(o =>
            o.Privileges["Upvote"] = new ModerationPrivilegeOptions { GroupId = MoSecurity.Voters, MinAccountAgeDays = 1 });
        using (var session = host.Store.OpenAsyncSession())
        {
            // An account from before #460 M5 whose backfill found no revision: CreatedAtUtc null.
            await session.StoreAsync(new MintPlayer.Spark.Authorization.Identity.SparkUser { UserName = "legacy" }, "users/legacy");
            await session.SaveChangesAsync();
        }
        var post = await host.SeedPostAsync(Alice);

        (await host.VoteAsync(Bob, post, 1)).Status.Should().Be(HttpStatusCode.OK, "CreatedAtUtc 365 days ago");
        (await host.VoteAsync("users/legacy", post, 1)).Status.Should().Be(HttpStatusCode.NotFound, "an unknown age fails closed");
    }

    // ---- author stamping ---------------------------------------------------------------------------

    [Fact]
    public async Task The_author_is_stamped_on_create_and_cannot_be_changed_by_an_edit()
    {
        await using var host = await StartAsync();

        var (created, body) = await host.SendAsync("/spark/po/create", MoHost.CreateBody(("Title", "mine"), ("AuthorId", "users/mallory")), Alice);
        created.Should().Be(HttpStatusCode.Created);
        var id = body.GetProperty("result").GetProperty("id").GetString()!;
        (await host.LoadAsync<MoPost>(id))!.AuthorId.Should().Be(Alice);

        (await host.SendAsync("/spark/po/update", MoHost.UpdateBody(id, await host.EtagAsync(id), ("Title", "edited"), ("AuthorId", Bob)), Alice)).Status.Should().Be(HttpStatusCode.OK);
        var stored = await host.LoadAsync<MoPost>(id);
        stored!.Title.Should().Be("edited");
        stored.AuthorId.Should().Be(Alice, "the author is the framework's");
    }
}
