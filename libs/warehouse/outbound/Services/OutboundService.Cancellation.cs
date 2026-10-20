using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Fulfilment.Contracts;
using MintPlayer.Spark.Warehouse.Models;
using MintPlayer.Spark.Warehouse.Outbound.Models;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Warehouse.Outbound.Services;

internal sealed partial class OutboundService
{
    internal const string RefusedShipped = "Already picked up by the courier.";
    internal const string RefusedPicked = "Already picked; cancellation after picking is switched off.";
    internal const string RefusedLabel = "The carrier label can no longer be voided.";

    /// <summary>
    /// The cancellation handshake (WD2, PRD §4.3), per state:
    /// <list type="bullet">
    /// <item>not yet picked: accepted; allocations released, tasks voided;</item>
    /// <item>picked, not packed: accepted with a put-back task per picked lot and tote, or refused (<c>Cancellation:AfterPick</c>, Q-W2);</item>
    /// <item>packed with a label: accepted only if the label can be voided (Packing's <see cref="IPackedOrderCancellation"/>);</item>
    /// <item>shipped: refused ("already picked up by the courier"), and the shop turns it into a return.</item>
    /// </list>
    /// Every step is idempotent (deterministic op ids, put-backs once per pick) and the answer is written last, so a crash
    /// mid-way is finished by the next delivery. The shop lowers <c>sold</c> only on the answer (§4.4 rule 1).
    /// </summary>
    public async Task<FulfilmentCancellationAnswer?> HandleAsync(string orderId, CancellationToken cancellationToken = default)
    {
        var order = await LoadOrderAsync(orderId, cancellationToken);
        if (order is null || !order.CancellationRequested || order.CancellationAnswer is not null || order.State == OutboundState.Rejected)
            return null;

        var scope = order.CancellationLineIds.Count == 0
            ? order.Lines.Select(l => l.LineId).ToHashSet(StringComparer.Ordinal)
            : order.CancellationLineIds.ToHashSet(StringComparer.Ordinal);
        bool InScope(OutboundLine line) => scope.Contains(line.LineId);

        if (order.Lines.Where(InScope).All(l => l.Remaining == 0) || order.State == OutboundState.Shipped)
            return await AnswerAsync(orderId, FulfilmentCancellationAnswer.Refused, RefusedShipped, cancellationToken);

        var pickedOnHand = order.Lines.Where(InScope).Any(l => l.PickedOnHand > 0);
        if (order.State is OutboundState.Packing or OutboundState.Packed)
        {
            if (packedCancellation is not null && !await packedCancellation.TryVoidAsync(order, cancellationToken))
                return await AnswerAsync(orderId, FulfilmentCancellationAnswer.Refused, RefusedLabel, cancellationToken);
        }
        if (pickedOnHand && Options.Cancellation.AfterPick == AfterPickCancellation.Refuse)
            return await AnswerAsync(orderId, FulfilmentCancellationAnswer.Refused, RefusedPicked, cancellationToken);

        // Accept. 1: the open pick tasks stop serving the cancelled lines.
        await VoidOrderWorkAsync(orderId, scope, cancellationToken);
        // 2: the unpicked allocations are released.
        await ReleaseOpenAllocationsAsync(orderId, null, "cancel", cancellationToken);
        // 3: a put-back per pick still in the warehouse (every zone tote included, W4).
        await CreatePutBacksAsync(orderId, scope, cancellationToken);
        // 4: the remaining units are cancelled and leave the channel's open quantity.
        order = (await LoadOrderAsync(orderId, cancellationToken))!;
        foreach (var line in order.Lines.Where(InScope).Where(l => l.Remaining > 0))
            await CancelUnitsAsync(orderId, line.LineId, line.Remaining, remainder: false, cancellationToken);
        // 5: the answer, last.
        return await AnswerAsync(orderId, FulfilmentCancellationAnswer.Accepted, null, cancellationToken);
    }

    private async Task<FulfilmentCancellationAnswer?> AnswerAsync(string orderId, FulfilmentCancellationAnswer answer, string? reason, CancellationToken cancellationToken)
    {
        var done = await ChangeOrderAsync(orderId, async (session, o) =>
        {
            if (o.CancellationAnswer is not null)
                return (false, false);
            o.CancellationAnswer = answer;
            o.CancellationRefusalReason = reason;
            if (answer == FulfilmentCancellationAnswer.Accepted)
            {
                foreach (var line in o.Lines)
                    line.Backordered = 0;
                if (o.Lines.All(l => l.Remaining == 0))
                    o.State = o.Lines.Any(l => l.Shipped > 0) ? OutboundState.Shipped : OutboundState.Cancelled;
                await FreeSlotAsync(session, o, cancellationToken);
            }
            return (true, true);
        }, cancellationToken);
        logger.LogInformation("Cancellation of {OrderId}: {Answer}{Reason}.", orderId, answer, reason is null ? "" : $" ({reason})");
        return done ? answer : null;
    }

    /// <summary>Removes the order's open allocations from its pick tasks; a task left with none is cancelled (a batch task keeps the other orders).</summary>
    private async Task VoidOrderWorkAsync(string orderId, IReadOnlySet<string> lineIds, CancellationToken cancellationToken)
    {
        var order = await LoadOrderAsync(orderId, cancellationToken);
        if (order is null)
            return;
        var taskIds = order.Lines.Where(l => lineIds.Contains(l.LineId)).SelectMany(l => l.Allocations)
            .Where(a => a.TaskId is not null && a.Quantity - a.Picked > 0).Select(a => a.TaskId!).Distinct(StringComparer.Ordinal).ToList();
        foreach (var taskId in taskIds)
        {
            await OutboundSessions.RetryAsync(store, async session =>
            {
                var task = await session.LoadAsync<WarehouseTask>(taskId, cancellationToken);
                if (task is null || task.State is WarehouseTaskState.Done or WarehouseTaskState.Cancelled)
                    return (false, false);
                var refs = AllocationsOf(task);
                var removed = refs.Where(r => r.OrderId == orderId && lineIds.Contains(r.LineId)).ToList();
                if (removed.Count == 0)
                    return (false, false);
                var o = await session.LoadAsync<OutboundOrder>(orderId, cancellationToken);
                var open = removed.Sum(r => o?.Line(r.LineId)?.Allocations.FirstOrDefault(a => a.Seq == r.Seq) is { } a ? a.Quantity - a.Picked : 0);
                SetAllocations(task, refs.Except(removed));
                task.Quantity = Math.Max(task.Confirmed, (task.Quantity ?? 0) - open);
                if (refs.Count == removed.Count || task.Confirmed >= task.Quantity)
                {
                    task.State = task.Confirmed > 0 ? WarehouseTaskState.Done : WarehouseTaskState.Cancelled;
                    task.CompletedAt = Now;
                }
                return (true, true);
            }, cancellationToken);
        }
    }

    /// <summary>One put-back task per pick still on hand (drop location or tote), back to where it was picked; created once per pick.</summary>
    private async Task CreatePutBacksAsync(string orderId, IReadOnlySet<string> lineIds, CancellationToken cancellationToken)
        => await ChangeOrderAsync(orderId, async (session, o) =>
        {
            var created = false;
            foreach (var line in o.Lines.Where(l => lineIds.Contains(l.LineId)))
            {
                foreach (var pick in line.Picks.Where(p => p.PutBackTaskId is null && p.Quantity - p.Shipped - p.PutBack > 0))
                {
                    var id = $"{WarehouseTask.IdPrefix}putback/{Uri.EscapeDataString(pick.OpId)}";
                    var location = await session.LoadAsync<WarehouseLocation>(pick.FromLocationId, cancellationToken);
                    var task = new WarehouseTask
                    {
                        Id = id,
                        Kind = WarehouseTaskKind.PutBack,
                        State = WarehouseTaskState.Open,
                        SiteId = o.SiteId!,
                        ZoneId = location?.ZoneId,
                        LocationId = pick.LocationId,
                        WalkSequence = location?.WalkSequence ?? 0,
                        TargetLocationId = pick.FromLocationId,
                        ItemId = line.ItemId,
                        LotCode = pick.LotCode,
                        Quantity = pick.Quantity - pick.Shipped - pick.PutBack,
                        OrganizationIds = o.OrganizationId is null ? [] : [o.OrganizationId],
                        Reference = OutboundReference,
                        Priority = 5,
                        CreatedAt = Now,
                    };
                    task.Payload[PayloadOrder] = o.Id!;
                    task.Payload[PayloadLine] = line.LineId;
                    task.Payload[PayloadPick] = pick.OpId;
                    if (pick.ToteId is not null)
                        task.Payload["tote"] = pick.ToteId;
                    await session.StoreAsync(task, string.Empty, id, cancellationToken);
                    pick.PutBackTaskId = id;
                    created = true;
                }
            }
            return (created, created);
        }, cancellationToken);

    /// <summary>Gives the order's consolidation slot back (shipped or cancelled), in the caller's session.</summary>
    internal static async Task FreeSlotAsync(IAsyncDocumentSession session, OutboundOrder order, CancellationToken cancellationToken)
    {
        if (order.ConsolidationSlotId is not { } slotId)
            return;
        var slot = await session.LoadAsync<ConsolidationSlot>(slotId, cancellationToken);
        if (slot is not null && slot.OrderId == order.Id)
        {
            slot.OrderId = null;
            slot.AssignedAt = null;
            slot.Expected = 0;
            slot.Arrived = 0;
            slot.Totes = [];
        }
        order.ConsolidationSlotId = null;
    }
}
