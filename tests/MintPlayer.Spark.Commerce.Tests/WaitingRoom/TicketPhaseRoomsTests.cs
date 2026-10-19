using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Commerce.Ticketing.Events.Models;
using MintPlayer.Spark.Commerce.WaitingRoom;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.WaitingRoom;
using static MintPlayer.Spark.Commerce.Tests.Events.EventFixtures;

namespace MintPlayer.Spark.Commerce.Tests.WaitingRoom;

using WaitingRoomMode = MintPlayer.Spark.WaitingRoom.WaitingRoomMode;

/// <summary>
/// The automatic room of a queued sale phase (item 7 of the #521 follow-ups): created from the phase's schedule and the
/// event's effective mode, updated on the next sync, ended when the queue is switched off.
/// </summary>
public class TicketPhaseRoomsTests : SparkTestDriver
{
    private readonly TestClock clock = new(Now);

    private TicketPhaseRooms Rooms(IServiceProvider scope, SparkCommerceOptions? commerce = null) => new(
        Store,
        scope.GetRequiredService<ISparkWaitingRooms>(),
        Monitor(commerce ?? CommerceOptions()),
        Monitor(new SparkCommerceWaitingRoomOptions { Admission = new WaitingRoomAdmissionSettings { MaxActive = 50 } }),
        clock,
        NullLogger<TicketPhaseRooms>.Instance);

    private async Task<TicketSalePhase> SeedAsync(bool queued = true)
    {
        var phase = Phase("TicketSalePhases/1-A");
        phase.QueueEnabled = queued;
        phase.StartsAt = Now.AddHours(1);
        phase.EndsAt = Now.AddHours(5);
        phase.QueueOpensAt = Now;
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(Event());
        await session.StoreAsync(phase);
        await session.SaveChangesAsync();
        return phase;
    }

    [Fact]
    public async Task A_queued_phase_gets_a_room_with_its_schedule_and_the_channels_mode()
    {
        using var harness = new WaitingRoomHarness(Store, clock);
        await using var scope = harness.As(new TestVisitor(null, null));
        var phase = await SeedAsync();

        var key = await Rooms(scope.ServiceProvider).SyncPhaseAsync(phase.Id!);

        key.Should().Be(CommerceWaitingRoomScopes.RoomKey(phase.Id!));
        var room = (await scope.ServiceProvider.GetRequiredService<ISparkWaitingRooms>().GetRoomAsync(key!))!.Value.Room;
        room.Gate.Should().Be(phase.Id);
        room.Mode.Should().Be(WaitingRoomMode.RandomizedPreQueue); // the channel's first mode; the seller chose none
        room.RequireSignIn.Should().BeTrue(); // D10
        room.OpensAt.Should().Be(phase.QueueOpensAt);
        room.StartsAt.Should().Be(phase.StartsAt);
        room.EndsAt.Should().Be(phase.EndsAt);
        room.CapacityKey.Should().Be(CommerceWaitingRoomScopes.TicketingPhasePrefix + phase.Id);
        room.Admission.MaxActive.Should().Be(50);
    }

    [Fact]
    public async Task Switching_the_queue_off_ends_the_room_and_an_unqueued_phase_gets_none()
    {
        using var harness = new WaitingRoomHarness(Store, clock);
        await using var scope = harness.As(new TestVisitor(null, null));
        var phase = await SeedAsync();
        var rooms = Rooms(scope.ServiceProvider);
        var key = await rooms.SyncPhaseAsync(phase.Id!);

        using (var session = Store.OpenAsyncSession())
        {
            (await session.LoadAsync<TicketSalePhase>(phase.Id))!.QueueEnabled = false;
            await session.SaveChangesAsync();
        }
        await rooms.SyncPhaseAsync(phase.Id!);

        var room = (await scope.ServiceProvider.GetRequiredService<ISparkWaitingRooms>().GetRoomAsync(key!))!.Value.Room;
        room.EndsAt.Should().Be(phase.StartsAt.AddSeconds(1)); // the sale had not started: right after the start

        (await rooms.SyncPhaseAsync("TicketSalePhases/404")).Should().BeNull();
    }

    [Fact]
    public async Task A_channel_without_waiting_room_modes_gets_no_room()
    {
        using var harness = new WaitingRoomHarness(Store, clock);
        await using var scope = harness.As(new TestVisitor(null, null));
        var phase = await SeedAsync();

        (await Rooms(scope.ServiceProvider, CommerceOptions(Channel(c => c.WaitingRoom.Modes = []))).SyncPhaseAsync(phase.Id!)).Should().BeNull();
    }
}
