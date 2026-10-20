using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.WaitingRoom;
using MintPlayer.Spark.WaitingRoom.Contracts;
using MintPlayer.Spark.WaitingRoom.Services;


namespace MintPlayer.Spark.Commerce.Tests.WaitingRoom;

using WaitingRoomMode = MintPlayer.Spark.WaitingRoom.WaitingRoomMode;

/// <summary>
/// The admission worker (§4.12): dense ranks, the seeded shuffle and its proof, admission limits, the presence check that
/// never costs the place (GA10, WCAG 2.2.1), the lease (failover, no double admission), truthful sold-out handling and the
/// ballot's staged windows. The clock starts at the real time because arrival order is the server's <c>@last-modified</c>.
/// </summary>
public class WaitingRoomWorkerTests : SparkTestDriver
{
    private readonly TestClock clock = new(TimeProvider.System.GetUtcNow());

    private WaitingRoomDefinition Room(WaitingRoomMode mode, int maxActive = 100, int perMinute = 1_000, TimeSpan? startsIn = null) => new()
    {
        Gate = "SalePhases/1",
        Mode = mode,
        RequireSignIn = mode != WaitingRoomMode.Fifo,
        OpensAt = clock.GetUtcNow().AddHours(-1),
        StartsAt = clock.GetUtcNow() + (startsIn ?? TimeSpan.FromMinutes(-1)),
        Admission = new WaitingRoomAdmissionSettings { MaxActive = maxActive, PerMinute = perMinute, TokenLifetime = TimeSpan.FromMinutes(20) },
        Ballot = new WaitingRoomBallotSettings { GroupSize = 2, WindowLength = TimeSpan.FromMinutes(10) },
    };

    private static async Task SaveAsync(WaitingRoomHarness harness, WaitingRoomDefinition room)
    {
        await using var scope = harness.As(new TestVisitor(null, null));
        await scope.ServiceProvider.GetRequiredService<ISparkWaitingRooms>().SaveRoomAsync("fest", room);
    }

    private static async Task<WaitingRoomJoinResult> JoinAsync(WaitingRoomHarness harness, TestVisitor visitor)
    {
        await using var scope = harness.As(visitor);
        return (await scope.ServiceProvider.GetRequiredService<ISparkWaitingRooms>().JoinAsync("fest", new WaitingRoomJoinBody()))!;
    }

    private static async Task<WaitingRoomEntryView?> PresenceAsync(WaitingRoomHarness harness, TestVisitor visitor, bool visible = true)
    {
        await using var scope = harness.As(visitor);
        return await scope.ServiceProvider.GetRequiredService<ISparkWaitingRooms>().PresenceAsync("fest", visible);
    }

    private static TestVisitor Guest(int i) => new($"guest-{i}", null);

    private static TestVisitor User(int i) => new($"browser-{i}", $"u{i}");

    [Fact]
    public async Task Fifo_ranks_are_dense_and_follow_server_arrival()
    {
        using var harness = new WaitingRoomHarness(Store, clock);
        await SaveAsync(harness, Room(WaitingRoomMode.Fifo, maxActive: 1));
        for (var i = 0; i < 12; i++)
            await JoinAsync(harness, Guest(i));
        clock.Advance(TimeSpan.FromMinutes(1));

        (await harness.TickAsync("fest")).Should().BeTrue();

        var entries = (await harness.EntriesAsync("fest")).OrderBy(e => e.Rank).ToList();
        entries.Select(e => e.Rank).Should().Equal(Enumerable.Range(1, 12).Select(i => (long?)i));
        entries.Zip(entries.Skip(1)).Should().OnlyContain(p => p.First.ArrivedAtUtc <= p.Second.ArrivedAtUtc);
        (await harness.StateAsync("fest")).RankedCount.Should().Be(12);
    }

    [Fact]
    public async Task The_pre_queue_is_shuffled_at_the_start_with_the_committed_seed_and_latecomers_queue_behind()
    {
        using var harness = new WaitingRoomHarness(Store, clock);
        await SaveAsync(harness, Room(WaitingRoomMode.RandomizedPreQueue, maxActive: 5, startsIn: TimeSpan.FromMinutes(10)));
        var commitment = (await harness.StateAsync("fest")).SeedCommitment;
        for (var i = 0; i < 30; i++)
            (await JoinAsync(harness, User(i))).Entry!.PreQueue.Should().BeTrue();

        await harness.TickAsync("fest");
        (await harness.EntriesAsync("fest")).Should().OnlyContain(e => e.Rank == null);
        (await harness.StateAsync("fest")).SeedRevealedAtUtc.HasValue.Should().BeFalse();

        clock.Advance(TimeSpan.FromMinutes(11));
        await harness.TickAsync("fest");

        var state = await harness.StateAsync("fest");
        state.Shuffled.Should().BeTrue();
        state.ShuffledCount.Should().Be(30);
        WaitingRoomShuffle.Commit(state.Seed).Should().Be(commitment);
        var entries = await harness.EntriesAsync("fest");
        entries.Select(e => e.Rank!.Value).Should().BeEquivalentTo(Enumerable.Range(1, 30).Select(i => (long)i));

        // Anyone can recompute the order from the published proof.
        WaitingRoomDrawProof proof;
        await using (var scope = harness.As(Guest(99)))
            proof = (await scope.ServiceProvider.GetRequiredService<ISparkWaitingRooms>().DrawProofAsync("fest"))!;
        proof.Seed.Should().Be(state.Seed);
        var recomputed = WaitingRoomShuffle.Order(proof.EntryIds!, proof.Seed!, out var inputHash);
        inputHash.Should().Be(proof.InputHash);
        foreach (var entry in entries)
            (recomputed.ToList().IndexOf(entry.Id!) + 1).Should().Be((int)entry.Rank!.Value);

        // The first five were called in the same tick; a latecomer queues behind everyone.
        entries.Count(e => e.Status == WaitingRoomEntryStatus.Called).Should().Be(5);
        state.ServedUpTo.Should().Be(5);
        var late = await JoinAsync(harness, User(100));
        late.Entry!.PreQueue.Should().BeFalse();
        clock.Advance(TimeSpan.FromSeconds(30));
        await harness.TickAsync("fest");
        (await harness.EntriesAsync("fest")).Single(e => e.Id == late.Entry.EntryId).Rank.Should().Be(31);
    }

    [Fact]
    public async Task A_missed_presence_check_keeps_the_place_and_a_visible_tab_is_admitted_by_itself()
    {
        using var harness = new WaitingRoomHarness(Store, clock);
        await SaveAsync(harness, Room(WaitingRoomMode.Fifo, maxActive: 3));
        for (var i = 1; i <= 8; i++)
            await JoinAsync(harness, Guest(i));
        // Guest 7's tab is hidden: it will get the long window when called.
        await PresenceAsync(harness, Guest(7), visible: false);
        clock.Advance(TimeSpan.FromMinutes(1));
        await harness.TickAsync("fest");

        var byRank = (await harness.EntriesAsync("fest")).ToDictionary(e => e.Rank!.Value);
        byRank[1].Status.Should().Be(WaitingRoomEntryStatus.Called);
        byRank[3].Status.Should().Be(WaitingRoomEntryStatus.Called);
        byRank[4].Status.Should().Be(WaitingRoomEntryStatus.Waiting);
        var guestAt = byRank.ToDictionary(p => p.Value.VisitorId!, p => p.Key);

        // A visible tab answers its check by itself: admitted, with a token for the room's gate.
        var admitted = await PresenceAsync(harness, new TestVisitor(byRank[1].VisitorId, null));
        admitted!.Status.Should().Be(WaitingRoomEntryStatus.Admitted);
        await using (var scope = harness.As(new TestVisitor(byRank[1].VisitorId, null)))
            (await scope.ServiceProvider.GetRequiredService<IWaitingRoomAdmissions>().ValidateAsync(admitted.AdmissionToken, "SalePhases/1")).IsValid.Should().BeTrue();

        // Ranks 2 and 3 do not answer within 15 s: missed, ranks kept; 4 and 5 are called in their place.
        clock.Advance(TimeSpan.FromSeconds(16));
        await harness.TickAsync("fest");
        byRank = (await harness.EntriesAsync("fest")).ToDictionary(e => e.Rank!.Value);
        byRank[2].Status.Should().Be(WaitingRoomEntryStatus.Missed);
        byRank[2].Misses.Should().Be(1);
        byRank[4].Status.Should().Be(WaitingRoomEntryStatus.Called);
        byRank[5].Status.Should().Be(WaitingRoomEntryStatus.Called);

        // Rank 2 comes back: waiting again at rank 2, so it is called before rank 6.
        var back = await PresenceAsync(harness, new TestVisitor(byRank[2].VisitorId, null));
        back!.Status.Should().Be(WaitingRoomEntryStatus.Waiting);
        back.Rank.Should().Be(2);
        clock.Advance(TimeSpan.FromSeconds(16));
        await harness.TickAsync("fest");
        byRank = (await harness.EntriesAsync("fest")).ToDictionary(e => e.Rank!.Value);
        byRank[2].Status.Should().Be(WaitingRoomEntryStatus.Called);
        byRank[6].Status.Should().Be(WaitingRoomEntryStatus.Called);
        byRank[7].Status.Should().Be(WaitingRoomEntryStatus.Waiting);

        // The hidden tab's check, when it comes, lasts the long window (≥ 20 s, WCAG 2.2.1).
        clock.Advance(TimeSpan.FromSeconds(16));
        await harness.TickAsync("fest");
        var hidden = (await harness.EntriesAsync("fest")).Single(e => e.Rank == guestAt["guest-7"]);
        hidden.Status.Should().Be(WaitingRoomEntryStatus.Called);
        (hidden.PresenceDeadlineUtc!.Value - hidden.CalledAtUtc!.Value).Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task The_per_minute_rate_bounds_admissions()
    {
        using var harness = new WaitingRoomHarness(Store, clock);
        await SaveAsync(harness, Room(WaitingRoomMode.Fifo, maxActive: 100, perMinute: 4));
        for (var i = 0; i < 10; i++)
            await JoinAsync(harness, Guest(i));
        clock.Advance(TimeSpan.FromMinutes(1));

        await harness.TickAsync("fest");
        clock.Advance(TimeSpan.FromSeconds(10));
        await harness.TickAsync("fest");
        (await harness.EntriesAsync("fest")).Count(e => e.Status == WaitingRoomEntryStatus.Called).Should().Be(4);

        clock.Advance(TimeSpan.FromMinutes(1));
        await harness.TickAsync("fest");
        (await harness.EntriesAsync("fest")).Count(e => e.Status is WaitingRoomEntryStatus.Called or WaitingRoomEntryStatus.Missed).Should().Be(8);
    }

    [Fact]
    public async Task One_instance_holds_a_room_at_a_time_and_another_takes_over_when_its_lease_lapses()
    {
        using var harness = new WaitingRoomHarness(Store, clock);
        await SaveAsync(harness, Room(WaitingRoomMode.Fifo));

        (await harness.TickAsync("fest", "node-a")).Should().BeTrue();
        (await harness.TickAsync("fest", "node-b")).Should().BeFalse();
        (await harness.TickAsync("fest", "node-a")).Should().BeTrue();

        // node-a dies: its lease (15 s) lapses and node-b takes the room over.
        clock.Advance(TimeSpan.FromSeconds(16));
        (await harness.TickAsync("fest", "node-b")).Should().BeTrue();
        (await harness.TickAsync("fest", "node-a")).Should().BeFalse();
    }

    [Fact]
    public async Task Two_workers_that_overlap_anyway_never_admit_twice()
    {
        using var harness = new WaitingRoomHarness(Store, clock);
        await SaveAsync(harness, Room(WaitingRoomMode.Fifo, maxActive: 5));
        for (var i = 0; i < 20; i++)
            await JoinAsync(harness, Guest(i));
        clock.Advance(TimeSpan.FromMinutes(1));
        await harness.TickAsync("fest"); // ranks 1..20 and calls 1..5
        foreach (var entry in (await harness.EntriesAsync("fest")).Where(e => e.Status == WaitingRoomEntryStatus.Called))
            await PresenceAsync(harness, new TestVisitor(entry.VisitorId, null));

        // Their admissions end; two instances (a lease lost mid-tick) call the next ones at the same moment.
        clock.Advance(TimeSpan.FromMinutes(21));
        await using var scopeA = harness.Services.CreateAsyncScope();
        await using var scopeB = harness.Services.CreateAsyncScope();
        var room = (await scopeA.ServiceProvider.GetRequiredService<ISparkWaitingRooms>().GetRoomAsync("fest"))!.Value.Room;
        var a = harness.Worker(scopeA.ServiceProvider, "node-a");
        var b = harness.Worker(scopeB.ServiceProvider, "node-b");
        await a.ExpireAsync(room, "fest", clock.GetUtcNow().UtcDateTime, CancellationToken.None);
        await Task.WhenAll(
            Task.Run(() => a.CallAsync(room, "fest", clock.GetUtcNow().UtcDateTime, CancellationToken.None)),
            Task.Run(() => b.CallAsync(room, "fest", clock.GetUtcNow().UtcDateTime, CancellationToken.None)));

        var entries = await harness.EntriesAsync("fest");
        entries.Count(e => e.Status == WaitingRoomEntryStatus.Called).Should().BeLessThanOrEqualTo(5);
        entries.Where(e => e.Status == WaitingRoomEntryStatus.Called).Select(e => e.Rank!.Value).Should().OnlyContain(r => r > 5 && r <= 10);
        (await harness.StateAsync("fest")).ServedUpTo.Should().BeLessThanOrEqualTo(10);
    }

    [Fact]
    public async Task Sold_out_is_truthful_stock_may_return_while_holds_are_outstanding()
    {
        using var harness = new WaitingRoomHarness(Store, clock);
        var room = Room(WaitingRoomMode.Fifo);
        room.CapacityKey = "quotas/fest";
        await SaveAsync(harness, room);
        for (var i = 0; i < 3; i++)
            await JoinAsync(harness, Guest(i));
        clock.Advance(TimeSpan.FromMinutes(1));

        harness.Capacity.Value = new WaitingRoomCapacity(0, 4);
        await harness.TickAsync("fest");
        (await harness.StateAsync("fest")).Status.Should().Be(WaitingRoomStatus.MayReturn);
        (await harness.EntriesAsync("fest")).Should().OnlyContain(e => e.Status == WaitingRoomEntryStatus.Waiting);

        harness.Capacity.Value = new WaitingRoomCapacity(2, 0);
        await harness.TickAsync("fest");
        (await harness.EntriesAsync("fest")).Count(e => e.Status == WaitingRoomEntryStatus.Called).Should().Be(2); // projected capacity: 2 units

        harness.Capacity.Value = new WaitingRoomCapacity(0, 0);
        clock.Advance(TimeSpan.FromSeconds(2));
        await harness.TickAsync("fest");
        (await harness.StateAsync("fest")).Status.Should().Be(WaitingRoomStatus.SoldOut);
        (await JoinAsync(harness, Guest(50))).Outcome.Should().Be("sold-out");
        harness.Topics.Public.Last().Payload.Should().Be(new WaitingRoomFrame("fest", WaitingRoomStatus.SoldOut, 2, 1,
            (await harness.StateAsync("fest")).SeedCommitment, null));
    }

    [Fact]
    public async Task A_ballot_draws_at_the_start_and_rolls_unused_places_into_the_next_window()
    {
        using var harness = new WaitingRoomHarness(Store, clock);
        var room = Room(WaitingRoomMode.Ballot, startsIn: TimeSpan.FromMinutes(5));
        room.AdmittedUrl = "https://tickets.example/buy?admission={token}";
        await SaveAsync(harness, room);
        for (var i = 0; i < 5; i++)
            (await JoinAsync(harness, User(i))).Outcome.Should().Be("joined");

        clock.Advance(TimeSpan.FromMinutes(6));
        (await JoinAsync(harness, User(9))).Outcome.Should().Be("registration-closed");
        await harness.TickAsync("fest");

        var entries = await harness.EntriesAsync("fest");
        entries.Select(e => e.Rank!.Value).Should().BeEquivalentTo(new long[] { 1, 2, 3, 4, 5 });
        var first = entries.Where(e => e.Status == WaitingRoomEntryStatus.Admitted).OrderBy(e => e.Rank).ToList();
        first.Select(e => e.Rank!.Value).Should().Equal(1L, 2L);
        harness.Topics.Private.Count(p => ((WaitingRoomPrivateFrame)p.Payload).Kind == "admitted").Should().Be(2);
        ((WaitingRoomPrivateFrame)harness.Topics.Private.First().Payload).AdmittedUrl.Should().StartWith("https://tickets.example/buy?admission=");

        // Rank 1 buys (spends its link); rank 2 lets its window pass.
        await using (var scope = harness.As(new TestVisitor("x", first[0].UserId)))
            (await scope.ServiceProvider.GetRequiredService<IWaitingRoomAdmissions>().ConsumeAsync(first[0].Token, "SalePhases/1", "baskets/1")).IsValid.Should().BeTrue();

        clock.Advance(TimeSpan.FromMinutes(10));
        await harness.TickAsync("fest");
        var byRank = (await harness.EntriesAsync("fest")).ToDictionary(e => e.Rank!.Value);
        byRank[1].Status.Should().Be(WaitingRoomEntryStatus.Done);
        byRank[2].Status.Should().Be(WaitingRoomEntryStatus.Done);
        // Window 2 invites its group of 2 plus rank 2's unused place: ranks 3, 4 and 5.
        new[] { 3L, 4L, 5L }.Should().OnlyContain(r => byRank[r].Status == WaitingRoomEntryStatus.Admitted);
        (await harness.StateAsync("fest")).ServedUpTo.Should().Be(5);
    }

    [Fact]
    public void Startup_refuses_a_hidden_tab_window_under_twenty_seconds()
    {
        var options = new SparkWaitingRoomOptions { HiddenPresenceWindow = TimeSpan.FromSeconds(10) };
        var act = () => SparkWaitingRoomExtensions.ValidateStartup(options);
        act.Should().Throw<InvalidOperationException>();
        SparkWaitingRoomExtensions.ValidateStartup(new SparkWaitingRoomOptions());
    }
}
