using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Ticketing.Events.Models;
using MintPlayer.Spark.Commerce.Ticketing.Events.Sales;
using MintPlayer.Spark.Commerce.WaitingRoom;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.WaitingRoom;
using Raven.Client.Documents;
using static MintPlayer.Spark.Commerce.Tests.Events.EventFixtures;

namespace MintPlayer.Spark.Commerce.Tests.WaitingRoom;

/// <summary>
/// The waiting room's capacity for an automatic phase room (<see cref="TicketPhaseWaitingRoomCapacity"/>, §4.12
/// "projected remaining capacity"): the inventory engine's quotas summed over the phase's dates, the phase's per-date
/// cap taken into account, and "unknown" (null) for seated dates, unprovisioned quotas and keys it does not own.
/// </summary>
public class TicketPhaseWaitingRoomCapacityTests : SparkTestDriver
{
    private const string PhaseId = "TicketSalePhases/1-A";
    private static readonly string Key = CommerceWaitingRoomScopes.TicketingPhasePrefix + PhaseId;
    private readonly TestClock clock = new(Now);

    private InventoryEngine Engine() => new(Store, options: new InventoryEngineOptions { Grace = TimeSpan.FromSeconds(1) }, clock: clock);

    private async Task SeedAsync(int? capacityPerDate = null, string? layoutSnapshotId = null)
    {
        var phase = Phase(PhaseId);
        phase.QueueEnabled = true;
        phase.CapacityPerDate = capacityPerDate;
        var day2 = Date("TicketEventDates/2-A", Now.AddDays(11), capacity: 5);
        day2.LayoutSnapshotId = layoutSnapshotId;
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(Event());
        await session.StoreAsync(Date("TicketEventDates/1-A", Now.AddDays(10), capacity: 10));
        await session.StoreAsync(day2);
        await session.StoreAsync(phase);
        await session.SaveChangesAsync();
        // The adapter's date query runs on an auto index: create it and let it catch up before the first read.
        await session.Query<TicketEventDate>()
            .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(10)))
            .Where(d => d.EventId == "TicketEvents/1-A")
            .ToListAsync();
    }

    [Fact]
    public async Task The_remaining_capacity_is_summed_over_the_phases_dates()
    {
        await SeedAsync();
        var engine = Engine();
        await engine.CreateQuotaAsync(TicketQuotas.Date("TicketEventDates/1-A"), 10, 1);
        await engine.CreateQuotaAsync(TicketQuotas.Date("TicketEventDates/2-A"), 5, 1);

        var capacity = await new TicketPhaseWaitingRoomCapacity(Store, engine).GetAsync(Key, default);

        capacity.Should().NotBeNull();
        capacity.Should().Be(new WaitingRoomCapacity(15, 0));
    }

    [Fact]
    public async Task The_phases_cap_per_date_bounds_each_date()
    {
        await SeedAsync(capacityPerDate: 3);
        var engine = Engine();
        await engine.CreateQuotaAsync(TicketQuotas.Date("TicketEventDates/1-A"), 10, 1);
        await engine.CreateQuotaAsync(TicketQuotas.Date("TicketEventDates/2-A"), 5, 1);
        await engine.CreateQuotaAsync(TicketQuotas.PhaseOnDate("TicketEventDates/1-A", PhaseId), 3, 1);
        await engine.CreateQuotaAsync(TicketQuotas.PhaseOnDate("TicketEventDates/2-A", PhaseId), 3, 1);

        var capacity = await new TicketPhaseWaitingRoomCapacity(Store, engine).GetAsync(Key, default);

        capacity.Should().NotBeNull();
        capacity.Should().Be(new WaitingRoomCapacity(6, 0));
    }

    [Fact]
    public async Task A_seated_date_an_unprovisioned_quota_or_a_foreign_key_is_unknown()
    {
        await SeedAsync(layoutSnapshotId: "LayoutSnapshots/1");
        var engine = Engine();
        await engine.CreateQuotaAsync(TicketQuotas.Date("TicketEventDates/1-A"), 10, 1);
        var capacity = new TicketPhaseWaitingRoomCapacity(Store, engine);

        (await capacity.GetAsync(Key, default)).Should().BeNull(); // seated: sold seat by seat
        (await capacity.GetAsync(CommerceWaitingRoomScopes.ForGate(PhaseId), default)).Should().BeNull();
        (await capacity.GetAsync(CommerceWaitingRoomScopes.TicketingPhasePrefix + "TicketSalePhases/404", default)).Should().BeNull();
    }

    [Fact]
    public async Task A_quota_that_is_not_provisioned_yet_is_unknown()
    {
        await SeedAsync();
        var engine = Engine();
        await engine.CreateQuotaAsync(TicketQuotas.Date("TicketEventDates/1-A"), 10, 1); // day 2 not provisioned

        (await new TicketPhaseWaitingRoomCapacity(Store, engine).GetAsync(Key, default)).Should().BeNull();
    }

}
