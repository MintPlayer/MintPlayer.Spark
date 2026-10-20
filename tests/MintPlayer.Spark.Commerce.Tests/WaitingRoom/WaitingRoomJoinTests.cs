using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Visitor;
using MintPlayer.Spark.Challenges;
using MintPlayer.Spark.Challenges.Altcha;
using MintPlayer.Spark.Challenges.Contracts;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.WaitingRoom;
using MintPlayer.Spark.WaitingRoom.Contracts;
using MintPlayer.Spark.WaitingRoom.Identity;
using Raven.Client.Documents.Session;


namespace MintPlayer.Spark.Commerce.Tests.WaitingRoom;

using WaitingRoomMode = MintPlayer.Spark.WaitingRoom.WaitingRoomMode;

/// <summary>
/// Joining (§4.12; S12 items S3 and S4 reduced per round 4: correctness, not load): one entry per identity however many
/// tabs and devices join at once; nobody refused for sharing an IPv4 address or an IPv6 /64; the merge on sign-in keeps
/// the better entry; D10's sign-in rule; the join challenge.
/// </summary>
public class WaitingRoomJoinTests : SparkTestDriver
{
    private readonly TestClock clock = new(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));

    private WaitingRoomDefinition Room(WaitingRoomMode mode = WaitingRoomMode.Fifo, bool signIn = false) => new()
    {
        Gate = "SalePhases/1",
        Mode = mode,
        RequireSignIn = signIn,
        OpensAt = clock.GetUtcNow().AddHours(-1),
        StartsAt = clock.GetUtcNow().AddHours(1),
    };

    private static async Task<WaitingRoomJoinResult> JoinAsync(WaitingRoomHarness harness, ISparkVisitor visitor, string address = "203.0.113.7", WaitingRoomJoinBody? body = null)
    {
        await using var scope = harness.As(visitor, address);
        return (await scope.ServiceProvider.GetRequiredService<ISparkWaitingRooms>().JoinAsync("fest", body ?? new WaitingRoomJoinBody()))!;
    }

    private async Task SaveAsync(WaitingRoomHarness harness, WaitingRoomDefinition room)
    {
        await using var scope = harness.As(new TestVisitor(null, null));
        await scope.ServiceProvider.GetRequiredService<ISparkWaitingRooms>().SaveRoomAsync("fest", room);
    }

    [Fact]
    public async Task Twenty_tabs_and_three_devices_make_one_entry_each_identity_with_the_same_position()
    {
        using var harness = new WaitingRoomHarness(Store, clock);
        await SaveAsync(harness, Room(WaitingRoomMode.RandomizedPreQueue, signIn: true));

        // 20 tabs of one signed-in browser and 3 more devices of the same account, all at once.
        var results = await Task.WhenAll(Enumerable.Range(0, 23).Select(i => Task.Run(() =>
            JoinAsync(harness, new TestVisitor(i < 20 ? "browser-1" : $"device-{i}", "u1")))));

        results.Should().OnlyContain(r => r.Outcome == "joined");
        results.Select(r => r.Entry!.EntryId).Distinct().Should().HaveCount(1);
        (await harness.EntriesAsync("fest")).Should().HaveCount(1);

        // A refresh an hour later answers the same entry.
        clock.Advance(TimeSpan.FromMinutes(30));
        (await JoinAsync(harness, new TestVisitor("device-22", "u1"))).Entry!.EntryId.Should().Be(results[0].Entry!.EntryId);
    }

    [Fact]
    public async Task Twenty_tabs_of_one_guest_cookie_make_one_entry()
    {
        using var harness = new WaitingRoomHarness(Store, clock);
        await SaveAsync(harness, Room());

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => JoinAsync(harness, new TestVisitor("cookie-1", null)))));

        results.Select(r => r.Entry!.EntryId).Distinct().Should().HaveCount(1);
        (await harness.EntriesAsync("fest")).Should().HaveCount(1);
    }

    [Fact]
    public async Task Nobody_is_refused_for_sharing_an_IPv4_address_or_an_IPv6_64()
    {
        using var harness = new WaitingRoomHarness(Store, clock, withChallenges: true);
        await SaveAsync(harness, Room());

        // 200 people behind one carrier NAT address and 100 inside one /64, with the risk pipeline on (S4 at CI size).
        var behindNat = Enumerable.Range(0, 200).Select(i => JoinAsync(harness, new TestVisitor($"nat-{i}", null), "198.51.100.10"));
        var inPrefix = Enumerable.Range(0, 100).Select(i => JoinAsync(harness, new TestVisitor($"v6-{i}", null), $"2001:db8:1:2::{i + 1:x}"));
        var results = new List<WaitingRoomJoinResult>();
        foreach (var join in behindNat.Concat(inPrefix))
            results.Add(await join);

        results.Should().OnlyContain(r => r.Outcome == "joined");
        (await harness.EntriesAsync("fest")).Should().HaveCount(300);
    }

    [Fact]
    public async Task A_fair_mode_without_sign_in_is_refused_unless_overridden()
    {
        using var harness = new WaitingRoomHarness(Store, clock);
        await using var scope = harness.As(new TestVisitor(null, null));
        var rooms = scope.ServiceProvider.GetRequiredService<ISparkWaitingRooms>();

        var shuffled = () => rooms.SaveRoomAsync("fest", Room(WaitingRoomMode.RandomizedPreQueue));
        await shuffled.Should().ThrowAsync<ArgumentException>();
        var ballot = () => rooms.SaveRoomAsync("fest", Room(WaitingRoomMode.Ballot));
        await ballot.Should().ThrowAsync<ArgumentException>();
        var overridden = Room(WaitingRoomMode.RandomizedPreQueue);
        overridden.AllowGuestsInFairModes = true;
        await rooms.SaveRoomAsync("fest", overridden);
        await rooms.SaveRoomAsync("fest-fifo", Room(WaitingRoomMode.Fifo));
    }

    [Fact]
    public async Task Sign_in_is_required_where_the_room_says_so_and_the_schedule_is_truthful()
    {
        using var harness = new WaitingRoomHarness(Store, clock);
        var room = Room(WaitingRoomMode.RandomizedPreQueue, signIn: true);
        room.OpensAt = clock.GetUtcNow().AddMinutes(10);
        await SaveAsync(harness, room);

        (await JoinAsync(harness, new TestVisitor("v1", null))).Outcome.Should().Be("sign-in-required");
        (await JoinAsync(harness, new TestVisitor("v1", "u1"))).Outcome.Should().Be("not-open");
        clock.Advance(TimeSpan.FromMinutes(10));
        (await JoinAsync(harness, new TestVisitor("v1", "u1"))).Outcome.Should().Be("joined");
    }

    [Fact]
    public async Task A_join_challenge_is_asked_once_and_never_again_for_the_entry()
    {
        using var harness = new WaitingRoomHarness(Store, clock, challenges: o => o.Altcha.MaxNumber = 1_000, withChallenges: true);
        var room = Room();
        room.ChallengeAtJoin = true;
        await SaveAsync(harness, room);
        var visitor = new TestVisitor("v1", null);

        var first = await JoinAsync(harness, visitor);
        first.Outcome.Should().Be("challenge");
        first.Challenge!.Provider.Should().Be(AltchaChallenge.ProviderName);

        var solved = new WaitingRoomJoinBody(new SparkChallengeResponse("altcha", ChallengeTestSupport.Solve(first.Challenge.Altcha!)));
        var joined = await JoinAsync(harness, visitor, body: solved);
        joined.Outcome.Should().Be("joined");

        // A refresh: the same entry, no challenge.
        (await JoinAsync(harness, visitor)).Outcome.Should().Be("joined");
        // The same solution from another visitor is a replay.
        (await JoinAsync(harness, new TestVisitor("v2", null), body: solved)).Outcome.Should().Be("challenge");
    }

    [Fact]
    public async Task Signing_in_moves_a_guest_entry_to_the_account_and_keeps_the_better_of_two()
    {
        using var harness = new WaitingRoomHarness(Store, clock);
        await SaveAsync(harness, Room());
        var guest = (await JoinAsync(harness, new TestVisitor("v1", null))).Entry!;
        clock.Advance(TimeSpan.FromSeconds(5));
        var account = (await JoinAsync(harness, new TestVisitor("phone", "u1"))).Entry!;
        guest.EntryId.Should().NotBe(account.EntryId);

        await MergeAsync("v1", "u1");

        var entries = await harness.EntriesAsync("fest");
        var kept = entries.Single(e => e.Status != WaitingRoomEntryStatus.Left);
        kept.Id.Should().Be(guest.EntryId); // the guest joined first: the earlier arrival wins
        kept.IdentityKey.Should().Be("user:u1");
        entries.Single(e => e.Id == account.EntryId).MergedInto.Should().Be(guest.EntryId);

        // Every device of the account now answers the kept entry; the guest cookie no longer has one.
        (await JoinAsync(harness, new TestVisitor("phone", "u1"))).Entry!.EntryId.Should().Be(guest.EntryId);
    }

    [Fact]
    public void The_better_entry_is_further_along_then_lower_ranked()
    {
        var admitted = new WaitingRoomEntry { Id = "a", Status = WaitingRoomEntryStatus.Admitted, Rank = 90 };
        var waiting = new WaitingRoomEntry { Id = "b", Status = WaitingRoomEntryStatus.Waiting, Rank = 3 };
        var later = new WaitingRoomEntry { Id = "c", Status = WaitingRoomEntryStatus.Waiting, Rank = 7 };
        var unranked = new WaitingRoomEntry { Id = "d", Status = WaitingRoomEntryStatus.Waiting };

        WaitingRoomVisitorMerge.Better(admitted, waiting).Should().Be(admitted);
        WaitingRoomVisitorMerge.Better(later, waiting).Should().Be(waiting);
        WaitingRoomVisitorMerge.Better(unranked, later).Should().Be(later);
    }

    private async Task MergeAsync(string visitorId, string userId)
    {
        using var session = Store.OpenAsyncSession(new SessionOptions { TransactionMode = TransactionMode.ClusterWide });
        await new WaitingRoomVisitorMerge().MergeAsync(visitorId, userId, session, CancellationToken.None);
        await session.SaveChangesAsync();
    }
}
