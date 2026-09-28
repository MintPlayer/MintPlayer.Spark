using System.Net;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Moderation;
using MintPlayer.Spark.Moderation.Documents;
using MintPlayer.Spark.Moderation.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Moderation;

/// <summary>
/// #460 §3.12 fraud measures 6 (nightly detector), 7 (compensating reversals), 8 (review surface) and
/// 9 (hashed network observations). Every scenario is driven by the host's clock.
/// </summary>
public class ModerationFraudTests : SparkTestDriver
{
    private const string Alice = "users/alice", Bob = "users/bob", Carol = "users/carol", Dave = "users/dave", Erin = "users/erin";

    private async Task<MoHost> StartAsync(Action<SparkModerationOptions>? configure = null)
    {
        var host = await MoHost.StartAsync(Store, o =>
        {
            // Caps are measure 4's test; here they would hide what the detector is looking at.
            o.Fraud.MaxCreditedPairVotesPerDay = 1000;
            o.Fraud.MaxCreditedPairVotesPer30Days = 1000;
            o.Fraud.MaxVotesCastPerDay = 1000;
            o.Fraud.CreditDelayHours = 0;
            configure?.Invoke(o);
        });
        foreach (var user in new[] { Alice, Bob, Carol, Dave, Erin })
            await host.SeedUserAsync(user);
        return host;
    }

    /// <summary>Posts by <paramref name="author"/>, created a day before the clock (so votes are not "fast").</summary>
    private static async Task<List<string>> PostsAsync(MoHost host, string author, int count)
    {
        var posts = new List<string>();
        for (var i = 0; i < count; i++)
            posts.Add(await host.SeedPostAsync(author, $"{author}-{i}", host.Clock.Now.AddDays(-1)));
        return posts;
    }

    private static async Task<int> CountAsync<T>(IDocumentStore store)
    {
        using var session = store.OpenAsyncSession();
        return await session.Query<T>().Customize(c => c.WaitForNonStaleResults()).CountAsync();
    }

    // ---- measure 6: serial -------------------------------------------------------------------------

    [Fact]
    public async Task M6_serial_voting_inside_24_hours_is_reversed_automatically_and_idempotently()
    {
        await using var host = await StartAsync();
        var alicePosts = await PostsAsync(host, Alice, 5);
        var erinPosts = await PostsAsync(host, Erin, 4);
        foreach (var post in alicePosts)
        {
            await host.VoteAsync(Bob, post, 1);
            host.Clock.Advance(TimeSpan.FromMinutes(20));
        }
        // Control: four votes spread 8 h apart — never five inside 24 h.
        foreach (var post in erinPosts)
        {
            await host.VoteAsync(Carol, post, 1);
            host.Clock.Advance(TimeSpan.FromHours(8));
        }
        await host.CreditAsync();
        (await host.SummaryAsync(Alice)).Total.Should().Be(50);

        var report = await host.DetectAsync();

        var caseId = ModerationIds.FraudCase("serial", Bob, Alice);
        report.ReversedCases.Should().Contain(caseId);
        report.ReversedCases.Should().NotContain(ModerationIds.FraudCase("serial", Carol, Erin));
        var reviewCase = await host.LoadAsync<ReviewCase>(caseId);
        reviewCase!.Status.Should().Be(ModerationCaseStatus.Reversed);
        reviewCase.VoteIds.Should().HaveCount(5);

        var reversals = (await host.EventsForAsync(Alice)).Where(e => e.Kind == ReputationEventKinds.Reversal).ToList();
        reversals.Should().HaveCount(5);
        reversals.Should().OnlyContain(e => e.RuleId == "serial" && e.CaseId == caseId && e.Points == -10);
        (await host.LoadAsync<ModerationTally>(ModerationIds.Tally(alicePosts[0])))!.Score.Should().Be(0);
        // The stored summary (what the privilege provider reads), with no recompute or crediting run:
        // the reversal itself lowered the total.
        var stored = await host.LoadAsync<ReputationSummary>(ModerationIds.Summary(Alice));
        stored!.Total.Should().Be(0, "a reversal of credited entries counts at once");
        stored.Pending.Should().Be(0);

        await host.CreditAsync();
        (await host.SummaryAsync(Alice)).Total.Should().Be(0);
        (await host.SummaryAsync(Erin)).Total.Should().Be(40, "the control pair is untouched");

        // Idempotent: a second run writes no second compensation.
        var before = await CountAsync<ReputationEvent>(host.Store);
        await host.DetectAsync();
        (await CountAsync<ReputationEvent>(host.Store)).Should().Be(before);
    }

    // ---- measure 6: concentration ------------------------------------------------------------------

    [Fact]
    public async Task M6_concentration_on_one_author_is_reversed_for_that_pair_only()
    {
        await using var host = await StartAsync();
        var alicePosts = await PostsAsync(host, Alice, 6);
        var erinPosts = await PostsAsync(host, Erin, 4);
        for (var i = 0; i < 6; i++)
        {
            await host.VoteAsync(Dave, alicePosts[i], 1);
            if (i < 4)
                await host.VoteAsync(Dave, erinPosts[i], 1);
            host.Clock.Advance(TimeSpan.FromDays(1)); // one a day: never serial
        }

        var report = await host.DetectAsync();

        report.ReversedCases.Should().Equal(ModerationIds.FraudCase("concentration", Dave, Alice));
        (await host.EventsForAsync(Alice)).Count(e => e.RuleId == "concentration").Should().Be(6, "6 of Dave's 10 votes (60 %) went to Alice");
        (await host.EventsForAsync(Erin)).Should().NotContain(e => e.Kind == ReputationEventKinds.Reversal);
    }

    // ---- measure 6: review-only rules ---------------------------------------------------------------

    [Fact]
    public async Task M6_reciprocal_voting_opens_a_case_and_reverses_nothing()
    {
        await using var host = await StartAsync();
        var bobPosts = await PostsAsync(host, Bob, 5);
        var carolPosts = await PostsAsync(host, Carol, 5);
        for (var i = 0; i < 5; i++)
        {
            await host.VoteAsync(Bob, carolPosts[i], 1);
            await host.VoteAsync(Carol, bobPosts[i], 1);
            host.Clock.Advance(TimeSpan.FromDays(1));
        }

        var report = await host.DetectAsync();

        var caseId = ModerationIds.FraudCase("reciprocal", Bob, Carol);
        report.OpenedCases.Should().Contain(caseId);
        report.ReversedCases.Should().BeEmpty();
        (await host.LoadAsync<ReviewCase>(caseId))!.Status.Should().Be(ModerationCaseStatus.Open);
        (await host.EventsForAsync(Bob)).Should().NotContain(e => e.Kind == ReputationEventKinds.Reversal, "NAT and colleagues look like this: a human decides");
    }

    [Fact]
    public async Task M6_fast_voting_right_after_posts_appear_opens_a_case()
    {
        await using var host = await StartAsync();
        for (var i = 0; i < 3; i++)
        {
            var post = await host.SeedPostAsync(Alice, $"fresh-{i}", host.Clock.Now);
            host.Clock.Advance(TimeSpan.FromSeconds(20));
            await host.VoteAsync(Erin, post, 1);
            host.Clock.Advance(TimeSpan.FromHours(9));
        }
        var slow = await host.SeedPostAsync(Alice, "slow", host.Clock.Now);
        host.Clock.Advance(TimeSpan.FromMinutes(5));
        await host.VoteAsync(Dave, slow, 1);

        var report = await host.DetectAsync();

        report.OpenedCases.Should().Contain(ModerationIds.FraudCase("fast-voting", Erin, Alice));
        report.OpenedCases.Should().NotContain(c => c.Contains(Dave));
    }

    [Fact]
    public async Task M6_a_registration_cluster_sharing_a_network_or_a_private_domain_opens_a_case()
    {
        await using var host = await StartAsync();
        var created = host.Clock.Now.UtcDateTime.AddDays(-20);
        await host.SeedUserAsync("users/x1", createdAtUtc: created, email: "x1@mail-one.test");
        await host.SeedUserAsync("users/x2", createdAtUtc: created.AddMinutes(30), email: "x2@mail-two.test");
        await host.SeedUserAsync("users/y1", createdAtUtc: created, email: "y1@acme.test");
        await host.SeedUserAsync("users/y2", createdAtUtc: created.AddMinutes(10), email: "y2@acme.test");
        await host.SeedUserAsync("users/z1", createdAtUtc: created, email: "z1@gmail.com");
        await host.SeedUserAsync("users/z2", createdAtUtc: created.AddMinutes(10), email: "z2@gmail.com");
        var x2Post = (await PostsAsync(host, "users/x2", 1))[0];
        var y2Post = (await PostsAsync(host, "users/y2", 1))[0];
        var z2Post = (await PostsAsync(host, "users/z2", 1))[0];

        // x1 and x2 are seen from the same /24; z1 and z2 from different networks.
        host.RemoteIp = "203.0.113.5";
        await host.VoteAsync("users/x1", x2Post, 1);
        host.RemoteIp = "203.0.113.77";
        await host.SendAsync("/spark/moderation/reputation", new { }, "users/x2");
        host.RemoteIp = "198.51.100.1";
        await host.VoteAsync("users/y1", y2Post, 1);
        await host.VoteAsync("users/z1", z2Post, 1);
        host.RemoteIp = "192.0.2.9";
        await host.SendAsync("/spark/moderation/reputation", new { }, "users/z2");
        host.RemoteIp = null;

        var report = await host.DetectAsync();

        report.OpenedCases.Should().Contain(ModerationIds.FraudCase("registration-cluster", "users/x1", "users/x2"));
        report.OpenedCases.Should().Contain(ModerationIds.FraudCase("registration-cluster", "users/y1", "users/y2"));
        report.OpenedCases.Should().NotContain(c => c.Contains("users/z1"), "a shared webmail domain is no signal");
    }

    // ---- measure 7: reversals are compensations ------------------------------------------------------

    [Fact]
    public async Task M7_the_target_sees_voting_corrected_without_the_voter()
    {
        await using var host = await StartAsync();
        foreach (var post in await PostsAsync(host, Alice, 5))
            await host.VoteAsync(Bob, post, 1);
        await host.DetectAsync();
        await host.CreditAsync();

        var (status, body) = await host.SendAsync("/spark/moderation/reputation/history", new { take = 50 }, Alice);

        status.Should().Be(HttpStatusCode.OK);
        var lines = body.GetProperty("result").EnumerateArray().ToList();
        lines.Should().Contain(l => l.GetProperty("label").GetString() == "Voting corrected (-50)");
        body.GetRawText().Should().NotContain(Bob, "the recipient never learns who voted");
    }

    [Fact]
    public async Task M7_deleting_an_account_reverses_its_votes_and_anonymises_the_ledger_idempotently()
    {
        await using var host = await StartAsync();
        var posts = await PostsAsync(host, Alice, 2);
        await host.VoteAsync(Bob, posts[0], 1);
        await host.VoteAsync(Bob, posts[1], -1);
        await host.VoteAsync(Carol, posts[0], 1);

        async Task DeleteBobAsync()
        {
            using var scope = host.Factory.CreateScope();
            var handler = scope.ServiceProvider.GetServices<ISparkAccountDeletionHandler<SparkUser>>().Single(h => h.GetType().Name.StartsWith("ModerationAccountDeletionHandler"));
            await handler.OnDeletingAccountAsync(new SparkUser { Id = Bob, UserName = "bob" }, CancellationToken.None);
        }
        await DeleteBobAsync();
        await DeleteBobAsync();

        var alice = await host.EventsForAsync(Alice);
        alice.Should().NotContain(e => e.VoterId == Bob, "anonymised");
        alice.Count(e => e.RuleId == "account-deleted").Should().Be(2, "one reversal per original entry, however often the handler runs");
        alice.Sum(e => e.Points).Should().Be(10, "only Carol's vote remains");
        (await host.EventsForAsync(Bob)).Should().BeEmpty("the down-vote's cost now names deleted-user");
        (await host.LoadAsync<ModerationVote>(ModerationIds.Vote(Bob, posts[0]))).Should().BeNull();
        (await host.LoadAsync<ModerationTally>(ModerationIds.Tally(posts[0])))!.Score.Should().Be(1);
    }

    [Fact]
    public async Task M7_merging_a_sock_puppet_reverses_every_vote_it_cast()
    {
        await using var host = await StartAsync();
        var posts = await PostsAsync(host, Alice, 3);
        foreach (var post in posts)
            await host.VoteAsync(Dave, post, 1);

        var (status, _) = await host.ModeratorAsync("/spark/moderation/merge", new { userId = Dave, intoUserId = Alice });
        var (denied, _) = await host.SendAsync("/spark/moderation/merge", new { userId = Carol, intoUserId = Alice }, Bob);

        status.Should().Be(HttpStatusCode.OK);
        denied.Should().Be(HttpStatusCode.NotFound);
        (await host.EventsForAsync(Alice)).Count(e => e.RuleId == "merge").Should().Be(3);
    }

    // ---- measure 8: the review surface ---------------------------------------------------------------

    [Fact]
    public async Task M8_a_case_shows_matrix_timeline_ages_networks_and_reputation_and_every_decision_is_audited()
    {
        await using var host = await StartAsync();
        var bobPosts = await PostsAsync(host, Bob, 5);
        var carolPosts = await PostsAsync(host, Carol, 5);
        host.RemoteIp = "203.0.113.10";
        for (var i = 0; i < 5; i++)
        {
            await host.VoteAsync(Bob, carolPosts[i], 1);
            await host.VoteAsync(Carol, bobPosts[i], 1);
            host.Clock.Advance(TimeSpan.FromDays(1));
        }
        host.RemoteIp = null;
        await host.CreditAsync();
        await host.DetectAsync();
        var caseId = ModerationIds.FraudCase("reciprocal", Bob, Carol);

        (await host.SendAsync("/spark/moderation/case", new { caseId }, Bob)).Status.Should().Be(HttpStatusCode.NotFound, "Bob has no Review right");
        var (status, body) = await host.ModeratorAsync("/spark/moderation/case", new { caseId });

        status.Should().Be(HttpStatusCode.OK);
        var detail = body.GetProperty("result");
        detail.GetProperty("case").GetProperty("kind").GetString().Should().Be("reciprocal");
        detail.GetProperty("matrix").EnumerateArray().Select(c => c.GetProperty("votes").GetInt32()).Should().Equal(5, 5);
        var timeline = detail.GetProperty("timeline").EnumerateArray().ToList();
        timeline.Should().HaveCount(10);
        timeline.Should().OnlyContain(t => t.GetProperty("secondsAfterPost").GetDouble() >= 86_400);
        var accounts = detail.GetProperty("accounts").EnumerateArray().ToList();
        accounts.Should().HaveCount(2);
        accounts.Should().OnlyContain(a => a.GetProperty("ageDays").GetInt32() >= 365);
        accounts.Should().OnlyContain(a => a.GetProperty("registrationMethod").GetString() == "password");
        accounts.Should().OnlyContain(a => a.GetProperty("sharesNetworkWith").GetInt32() == 1, "Bob and Carol were seen from one network");
        accounts.Should().OnlyContain(a => a.GetProperty("reputationBefore").GetInt32() == 50 && a.GetProperty("reputationAfter").GetInt32() == 0);
        body.GetRawText().Should().NotContain("203.0.113", "never the address");

        (await host.ModeratorAsync("/spark/moderation/case/decide", new { caseId, decision = "reverse", reason = "ring" })).Status.Should().Be(HttpStatusCode.OK);
        (await host.ModeratorAsync("/spark/moderation/case/decide", new { caseId, decision = "dismiss" })).Status.Should().Be(HttpStatusCode.BadRequest, "already decided");
        (await host.EventsForAsync(Carol)).Count(e => e.RuleId == "moderator" && e.CaseId == caseId).Should().Be(5);

        var (_, audit) = await host.ModeratorAsync("/spark/moderation/audit", new { take = 50 });
        audit.GetProperty("result").EnumerateArray().Should().Contain(e => e.GetProperty("action").GetString() == "decide:reverse" && e.GetProperty("caseId").GetString() == caseId);
    }

    // ---- measure 9: hashed networks ---------------------------------------------------------------

    [Fact]
    public void M9_addresses_are_truncated_to_the_24_and_48_networks_before_hashing()
    {
        ModerationIpHasher.Truncate(IPAddress.Parse("203.0.113.77")).Should().Equal((byte)203, (byte)0, (byte)113);
        ModerationIpHasher.Truncate(IPAddress.Parse("::ffff:203.0.113.77")).Should().Equal((byte)203, (byte)0, (byte)113);
        ModerationIpHasher.Truncate(IPAddress.Parse("2001:db8:abcd:1234::1")).Should().Equal(
            (byte)0x20, (byte)0x01, (byte)0x0d, (byte)0xb8, (byte)0xab, (byte)0xcd);
    }

    [Fact]
    public async Task M9_observations_store_a_rotating_keyed_hash_that_expires_and_never_the_address()
    {
        await using var host = await StartAsync(o =>
        {
            o.Fraud.IpKeyRotationDays = 30;
            o.Fraud.IpObservationRetentionDays = 90;
        });
        (string KeyId, string Hash) Hash(string ip)
        {
            using var scope = host.Factory.CreateScope();
            return scope.ServiceProvider.GetRequiredService<ModerationIpHasher>().HashAsync(IPAddress.Parse(ip)).GetAwaiter().GetResult();
        }

        var a = Hash("203.0.113.5");
        var sameNetwork = Hash("203.0.113.250");
        var otherNetwork = Hash("203.0.114.5");
        sameNetwork.Should().Be(a);
        otherNetwork.Hash.Should().NotBe(a.Hash);

        host.RemoteIp = "203.0.113.5";
        await host.SendAsync("/spark/moderation/reputation", new { }, Bob);
        host.RemoteIp = null;
        using (var session = host.Store.OpenAsyncSession())
        {
            var observation = await session.Query<ModerationIpObservation>().Customize(c => c.WaitForNonStaleResults()).Where(o => o.UserId == Bob).SingleAsync();
            observation.Hash.Should().Be(a.Hash);
            var expires = DateTime.Parse((string)session.Advanced.GetMetadataFor(observation)["@expires"], null, System.Globalization.DateTimeStyles.RoundtripKind);
            expires.Should().Be(host.Clock.Now.UtcDateTime.AddDays(90));

            var key = await session.LoadAsync<ModerationIpKey>(a.KeyId);
            key!.ProtectedKey.Should().NotBeNullOrEmpty();
            ((string)session.Advanced.GetMetadataFor(key)["@expires"]).Should().NotBeNullOrEmpty("a key outlives its last observation by the retention, then goes");
        }
        var raw = await ObservationsJsonAsync(host.Store);
        raw.Should().NotContain("203.0.113");

        // Rotation: the same address hashes differently in the next period.
        host.Clock.Advance(TimeSpan.FromDays(30));
        var rotated = Hash("203.0.113.5");
        rotated.KeyId.Should().NotBe(a.KeyId);
        rotated.Hash.Should().NotBe(a.Hash);
    }

    private static async Task<string> ObservationsJsonAsync(IDocumentStore store)
    {
        using var session = store.OpenAsyncSession();
        var docs = await session.Query<ModerationIpObservation>().Customize(c => c.WaitForNonStaleResults()).ToListAsync();
        return System.Text.Json.JsonSerializer.Serialize(docs);
    }
}
