using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.WaitingRoom;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.WaitingRoom;
using MintPlayer.Spark.WaitingRoom.Admissions;
using MintPlayer.Spark.WaitingRoom.Identity;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Tests.WaitingRoom;

using WaitingRoomMode = MintPlayer.Spark.WaitingRoom.WaitingRoomMode;

/// <summary>
/// The commerce bridge (item 7 of the #521 follow-ups) and the waiting room's GDPR hooks (item 8): admission tokens as
/// Checkout's gate, Promotions' queue state, the phase room keys; export and erasure through the account hooks.
/// </summary>
public class CommerceWaitingRoomBridgeTests : SparkTestDriver
{
    private const string Phase = "SalePhases/1";
    private static readonly string Scope = CommerceWaitingRoomScopes.TicketingPhasePrefix + Phase;
    private readonly TestClock clock = new(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));

    private string Issue(WaitingRoomHarness harness, string subject, string phase = Phase)
        => harness.Services.GetRequiredService<WaitingRoomTokens>()
            .Issue("WaitingRooms/fest", phase, "WaitingRoomEntries/" + Guid.NewGuid().ToString("N"), subject, clock.GetUtcNow().AddMinutes(30), poisoned: false).Token;

    private static AdmissionCheck Check(string scope, string? token, string? user, bool consume, string? visitor = null, string item = "tickets/1")
        => new(scope, token, user, visitor, item, 1, consume);

    // ---- the gate ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_gate_owns_ticketing_phase_and_waiting_room_scopes_only()
    {
        using var harness = new WaitingRoomHarness(Store, clock);
        await using var scope = harness.As(new TestVisitor(null, "u1"));
        var gate = new WaitingRoomCheckoutGate(scope.ServiceProvider.GetRequiredService<IWaitingRoomAdmissions>());

        gate.Handles(Scope).Should().BeTrue();
        gate.Handles(CommerceWaitingRoomScopes.ForGate("drops/1")).Should().BeTrue();
        gate.Handles("promotions:presale").Should().BeFalse();
        gate.Handles(CommerceWaitingRoomScopes.TicketingPhasePrefix).Should().BeFalse();
    }

    [Fact]
    public async Task One_admission_covers_every_line_of_the_admitted_buyer_and_nobody_else()
    {
        using var harness = new WaitingRoomHarness(Store, clock);
        var token = Issue(harness, "user:u1");
        await using var scope = harness.As(new TestVisitor(null, "u1"));
        var gate = new WaitingRoomCheckoutGate(scope.ServiceProvider.GetRequiredService<IWaitingRoomAdmissions>());

        (await gate.CheckAsync(Check(Scope, token, "u1", consume: false), default)).Admitted.Should().BeTrue();
        (await gate.CheckAsync(Check(Scope, token, "u1", consume: true), default)).Admitted.Should().BeTrue();
        // A second ticket type, then the re-check before consuming again: the same buyer is the same consumer.
        (await gate.CheckAsync(Check(Scope, token, "u1", consume: false, item: "tickets/2"), default)).Admitted.Should().BeTrue();
        (await gate.CheckAsync(Check(Scope, token, "u1", consume: true, item: "tickets/2"), default)).Admitted.Should().BeTrue();

        var stolen = await gate.CheckAsync(Check(Scope, token, "u2", consume: true), default);
        stolen.Admitted.Should().BeFalse();
        stolen.Reason.Should().Be(WaitingRoomRefusals.WrongIdentity); // the token is bound to u1
    }

    [Fact]
    public async Task A_token_for_another_phase_or_without_a_buyer_is_refused()
    {
        using var harness = new WaitingRoomHarness(Store, clock);
        var token = Issue(harness, "user:u1", phase: "SalePhases/2");
        await using var scope = harness.As(new TestVisitor(null, "u1"));
        var gate = new WaitingRoomCheckoutGate(scope.ServiceProvider.GetRequiredService<IWaitingRoomAdmissions>());

        (await gate.CheckAsync(Check(Scope, token, "u1", consume: true), default)).Reason.Should().Be(WaitingRoomRefusals.WrongPhase);
        (await gate.CheckAsync(Check(Scope, token, null, consume: false), default)).Reason.Should().Be(WaitingRoomRefusals.NoBuyer);
        (await gate.CheckAsync(Check(Scope, null, "u1", consume: false), default)).Reason.Should().Be(WaitingRoomRefusals.Missing);
    }

    [Fact]
    public async Task A_waiting_room_scope_names_the_rooms_gate()
    {
        using var harness = new WaitingRoomHarness(Store, clock);
        var token = Issue(harness, "visitor:v1", phase: "drops/autumn");
        await using var scope = harness.As(new TestVisitor("v1", null));
        var gate = new WaitingRoomCheckoutGate(scope.ServiceProvider.GetRequiredService<IWaitingRoomAdmissions>());

        (await gate.CheckAsync(Check(CommerceWaitingRoomScopes.ForGate("drops/autumn"), token, null, consume: true, visitor: "v1"), default))
            .Admitted.Should().BeTrue();
    }

    // ---- room keys and queue state ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("SalePhases/1")]
    [InlineData("TicketSalePhases/1-A")]
    [InlineData("phases/Ünïcode & spaces")]
    public void A_phase_room_key_is_a_valid_room_key(string phaseId)
    {
        var key = CommerceWaitingRoomScopes.RoomKey(phaseId);
        key.Should().MatchRegex("^[a-z0-9][a-z0-9._-]{0,99}$");
        CommerceWaitingRoomScopes.RoomKey(phaseId).Should().Be(key);
    }

    [Fact]
    public void Phase_ids_that_fold_to_the_same_text_get_different_rooms()
        => CommerceWaitingRoomScopes.RoomKey("Phases/1").Should().NotBe(CommerceWaitingRoomScopes.RoomKey("phases-1"));

    [Fact]
    public async Task The_waiting_list_waits_until_the_phase_room_is_sold_out_for_good()
    {
        using var harness = new WaitingRoomHarness(Store, clock);
        await using var scope = harness.As(new TestVisitor(null, null));
        var rooms = scope.ServiceProvider.GetRequiredService<ISparkWaitingRooms>();
        var state = new WaitingListQueueState(rooms, Store, clock);
        var key = CommerceWaitingRoomScopes.RoomKey(Phase);

        state.Handles(Scope).Should().BeTrue();
        (await state.IsQueueClosedAsync(Scope, default)).Should().BeFalse(); // no room yet: not closed

        await rooms.SaveRoomAsync(key, new WaitingRoomDefinition
        {
            Gate = Phase, Mode = WaitingRoomMode.Fifo, StartsAt = clock.GetUtcNow(), EndsAt = clock.GetUtcNow().AddHours(2),
        });
        (await state.IsQueueClosedAsync(Scope, default)).Should().BeFalse();

        await rooms.MarkSoldOutAsync(key, soldOut: true);
        (await state.IsQueueClosedAsync(Scope, default)).Should().BeTrue();

        await rooms.MarkSoldOutAsync(key, soldOut: false);
        clock.Advance(TimeSpan.FromHours(2));
        (await state.IsQueueClosedAsync(Scope, default)).Should().BeTrue(); // the room's end passed
    }

    // ---- GDPR (item 8) ----------------------------------------------------------------------------------------------

    private static WaitingRoomDefinition FifoRoom(TestClock clock) => new()
    {
        Gate = Phase, Mode = WaitingRoomMode.Fifo, OpensAt = clock.GetUtcNow().AddHours(-1), StartsAt = clock.GetUtcNow().AddHours(1),
    };

    [Fact]
    public async Task The_export_lists_the_accounts_entries_without_the_token()
    {
        using var harness = new WaitingRoomHarness(Store, clock);
        await using (var admin = harness.As(new TestVisitor(null, null)))
            await admin.ServiceProvider.GetRequiredService<ISparkWaitingRooms>().SaveRoomAsync("fest", FifoRoom(clock));
        await using (var buyer = harness.As(new TestVisitor("v1", "users/1")))
            (await buyer.ServiceProvider.GetRequiredService<ISparkWaitingRooms>().JoinAsync("fest", new()))!.Outcome.Should().Be("joined");
        await WaitForIndexesAsync();

        var export = await new WaitingRoomPersonalData<SparkUser>(Store).GetPersonalDataAsync(new SparkUser { Id = "users/1" }, default);
        var json = System.Text.Json.JsonSerializer.Serialize(export);
        json.Should().Contain("\"Room\":\"fest\"");
        json.Should().NotContain("Token");

        (await new WaitingRoomPersonalData<SparkUser>(Store).GetPersonalDataAsync(new SparkUser { Id = "users/2" }, default)).Should().BeNull();
    }

    [Fact]
    public async Task Erasure_anonymises_the_entry_releases_the_identity_and_keeps_the_rank()
    {
        using var harness = new WaitingRoomHarness(Store, clock);
        await using (var admin = harness.As(new TestVisitor(null, null)))
            await admin.ServiceProvider.GetRequiredService<ISparkWaitingRooms>().SaveRoomAsync("fest", FifoRoom(clock));
        string entryId;
        await using (var buyer = harness.As(new TestVisitor("v1", "users/1")))
            entryId = (await buyer.ServiceProvider.GetRequiredService<ISparkWaitingRooms>().JoinAsync("fest", new()))!.Entry!.EntryId;
        await WaitForIndexesAsync();

        var deletion = new WaitingRoomAccountDeletion<SparkUser>(Store);
        await deletion.OnDeletingAccountAsync(new SparkUser { Id = "users/1" }, default);
        await deletion.OnDeletingAccountAsync(new SparkUser { Id = "users/1" }, default); // idempotent (a retried deletion)

        using (var session = Store.OpenAsyncSession())
        {
            var entry = (await session.LoadAsync<WaitingRoomEntry>(entryId))!;
            entry.UserId.Should().BeNull();
            entry.VisitorId.Should().BeNull();
            entry.Token.Should().BeNull();
            entry.IdentityKey.Should().Be("erased:" + entryId);
            entry.Status.Should().Be(WaitingRoomEntryStatus.Left);
        }

        // The identity key was released: the same id joining again gets a new entry, at the back.
        await using (var again = harness.As(new TestVisitor("v1", "users/1")))
            (await again.ServiceProvider.GetRequiredService<ISparkWaitingRooms>().JoinAsync("fest", new()))!.Entry!.EntryId.Should().NotBe(entryId);
    }
}
