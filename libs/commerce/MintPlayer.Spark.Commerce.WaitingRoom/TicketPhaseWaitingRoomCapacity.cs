using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Ticketing.Events.Models;
using MintPlayer.Spark.Commerce.Ticketing.Events.Sales;
using MintPlayer.Spark.WaitingRoom;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Commerce.WaitingRoom;

/// <summary>
/// The waiting room's <see cref="IWaitingRoomCapacity"/> for a ticketing sale phase (§4.12 "projected remaining
/// capacity", "Sold out"): the capacity key <c>ticketing-phase:{phaseId}</c> (what an automatic phase room carries) is
/// answered from the inventory engine, summed over the phase's dates. A date sells the smaller of its general-admission
/// quota and the phase's own cap on that date; holds count from the same quotas.
/// </summary>
/// <remarks>
/// A seated date (a layout snapshot) is sold seat by seat, not from a counted quota: a phase with a seated date answers
/// null (unknown), so its room never sells out by itself and staff (or the host) call <c>MarkSoldOutAsync</c>. A quota
/// not provisioned yet (the event is not published) also answers null.
/// </remarks>
internal sealed class TicketPhaseWaitingRoomCapacity(IDocumentStore store, ISparkInventory inventory) : IWaitingRoomCapacity
{
    public async Task<WaitingRoomCapacity?> GetAsync(string capacityKey, CancellationToken cancellationToken)
    {
        if (!capacityKey.StartsWith(CommerceWaitingRoomScopes.TicketingPhasePrefix, StringComparison.Ordinal)
            || !CommerceWaitingRoomScopes.TryGetGate(capacityKey, out var phaseId))
            return null;

        TicketSalePhase? phase;
        List<TicketEventDate> dates;
        using (var session = store.OpenAsyncSession())
        {
            phase = await session.LoadAsync<TicketSalePhase>(phaseId, cancellationToken);
            if (phase is null)
                return null;
            var eventId = phase.EventId;
            dates = await session.Query<TicketEventDate>()
                .Where(d => d.EventId == eventId)
                .Take(1024)
                .ToListAsync(cancellationToken);
        }
        var sold = SaleWindows.PhaseDates(phase, dates.Select(d => d.Id!)).ToHashSet(StringComparer.Ordinal);
        var phaseDates = dates.Where(d => sold.Contains(d.Id!)).ToList();
        if (phaseDates.Count == 0 || phaseDates.Any(d => !string.IsNullOrEmpty(d.LayoutSnapshotId)))
            return null;

        long remaining = 0, holds = 0;
        foreach (var date in phaseDates)
        {
            if (await TryAsync(TicketQuotas.Date(date.Id!), cancellationToken) is not { } dateQuota)
                return null;
            var available = dateQuota.Available;
            var held = dateQuota.Held;
            if (phase.CapacityPerDate is not null)
            {
                if (await TryAsync(TicketQuotas.PhaseOnDate(date.Id!, phase.Id!), cancellationToken) is not { } phaseQuota)
                    return null;
                available = Math.Min(available, phaseQuota.Available);
                held = Math.Max(held, phaseQuota.Held);
            }
            remaining += Math.Max(0, available);
            holds += Math.Max(0, held);
        }
        return new WaitingRoomCapacity(remaining, holds);
    }

    private async Task<QuotaAvailability?> TryAsync(string quotaId, CancellationToken cancellationToken)
    {
        try
        {
            return await inventory.GetAvailabilityAsync(quotaId, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return null; // not provisioned (yet)
        }
    }
}
