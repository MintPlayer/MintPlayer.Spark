using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Fulfilment.Contracts;
using MintPlayer.Spark.Warehouse.Indexes;
using MintPlayer.Spark.Warehouse.Models;
using MintPlayer.Spark.Warehouse.Outbound.Models;
using MintPlayer.Spark.Warehouse.Stock;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Warehouse.Outbound.Services;

internal sealed partial class OutboundService
{
    public async Task<ReleaseReport> ReleaseAsync(IReadOnlyList<string> orderIds, string? waveId = null, string? strategy = null, string? userId = null,
        CancellationToken cancellationToken = default)
    {
        var released = new List<string>();
        var waiting = new Dictionary<string, ReleaseBlock>(StringComparer.Ordinal);
        foreach (var orderId in orderIds.Distinct(StringComparer.Ordinal))
        {
            var block = await AllocateOrderAsync(orderId, waveId, excluded: null, userId, cancellationToken);
            if (block is null)
                released.Add(orderId);
            else
                waiting[orderId] = block.Value;
        }

        var groups = new List<string>();
        foreach (var site in await UnplannedBySiteAsync(released, cancellationToken))
        {
            var planned = await PlanWorkAsync(site.Key, site.Value, waveId, strategy, cancellationToken);
            if (planned is null)
            {
                foreach (var orderId in site.Value.Select(l => l.OrderId).Distinct())
                {
                    released.Remove(orderId);
                    waiting[orderId] = ReleaseBlock.NoDropLocation;
                }
                continue;
            }
            groups.AddRange(planned);
        }
        return new ReleaseReport(released, waiting, groups);
    }

    public Task<bool> HoldAsync(string orderId, bool hold, string? reason, CancellationToken cancellationToken = default)
        => ChangeOrderAsync(orderId, (_, o) =>
        {
            if (o.IsClosed || o.ManualHold == hold)
                return Task.FromResult((false, false));
            o.ManualHold = hold;
            o.HoldReason = hold ? reason : null;
            return Task.FromResult((true, true));
        }, cancellationToken);

    /// <summary>
    /// Allocates what an order still needs (PRD §4.7 Allocation): null when it has work (or had it already), else why not.
    /// The first release of an order checks the shop's holds, <c>ShipNotBefore</c> in the site's local date, and
    /// <c>ShipComplete</c>; a quantity no stock covers is backordered or cancelled per the shop's policy.
    /// </summary>
    private async Task<ReleaseBlock?> AllocateOrderAsync(string orderId, string? waveId, IReadOnlyCollection<string>? excluded, string? userId, CancellationToken cancellationToken)
    {
        var order = await LoadOrderAsync(orderId, cancellationToken);
        if (order is null || order.IsClosed || order.SiteId is null || order.OrganizationId is null)
            return ReleaseBlock.NotReleasable;
        if (order.CancellationRequested && order.CancellationAnswer is null)
            return ReleaseBlock.CancellationRequested;
        if (order.Holds.Count > 0)
            return ReleaseBlock.ShopHold;
        if (order.ManualHold)
            return ReleaseBlock.ManualHold;
        if (order.State is OutboundState.Packing or OutboundState.Packed)
            return ReleaseBlock.NotReleasable;

        DateOnly today;
        using (var session = store.OpenAsyncSession())
            today = await SiteTodayAsync(session, order.SiteId, cancellationToken);
        if (order.ShipNotBefore is { } notBefore && notBefore > today)
            return ReleaseBlock.NotBefore;

        if (order.State == OutboundState.Accepted && order.ShipComplete)
        {
            // ShipComplete (§4.3): stay unallocated until every line can be allocated. Candidates are a stale-tolerant
            // estimate; the claims below decide, and a shortfall rolls them back.
            foreach (var line in order.Lines.Where(l => l.Unallocated > 0))
            {
                var available = (await CandidatesAsync(order, line, today, excluded, cancellationToken)).Sum(c => c.Candidate.Available);
                if (available < line.Unallocated)
                    return ReleaseBlock.ShipComplete;
            }
        }

        foreach (var line in order.Lines.Where(l => l.Unallocated > 0).ToList())
            await AllocateLineAsync(orderId, line.LineId, today, excluded, userId, cancellationToken);

        order = (await LoadOrderAsync(orderId, cancellationToken))!;
        if (order.ShipComplete && order.Lines.Any(l => l.Unallocated > 0) && order.Lines.All(l => l.Picks.Count == 0))
        {
            // Lost a race for the last units: give the claims back and wait (ShipComplete never ships part).
            await ReleaseOpenAllocationsAsync(orderId, null, "ship-complete", cancellationToken);
            return ReleaseBlock.ShipComplete;
        }

        await ApplyRemainderPolicyAsync(orderId, cancellationToken);

        return await ChangeOrderAsync(orderId, (_, o) =>
        {
            var hasWork = o.Lines.Any(l => l.OpenAllocated > 0 || l.PickedOnHand > 0);
            if (o.State == OutboundState.Accepted && hasWork)
            {
                o.State = OutboundState.Released;
                o.ReleasedAt = Now;
                o.WaveId = waveId;
                return Task.FromResult<(ReleaseBlock?, bool)>((null, true));
            }
            return Task.FromResult<(ReleaseBlock?, bool)>((hasWork || o.IsClosed ? null : ReleaseBlock.NotReleasable, false));
        }, cancellationToken);
    }

    /// <summary>Allocation candidates of a line: sellable balances with stock available at the order's site, from <c>Warehouse/Stock</c>.</summary>
    private async Task<List<(AllocationCandidate Candidate, StockBalance Balance)>> CandidatesAsync(OutboundOrder order, OutboundLine line, DateOnly today,
        IReadOnlyCollection<string>? excluded, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var balances = await StockQueries.CandidatesAsync(session, order.SiteId!, line.ItemId, [order.OrganizationId!], cancellationToken);
        var result = new List<(AllocationCandidate, StockBalance)>();
        var locations = await session.LoadAsync<WarehouseLocation>(balances.Select(b => b.LocationId).Distinct(), cancellationToken);
        foreach (var balance in balances)
        {
            if (excluded is not null && excluded.Contains(balance.Id!))
                continue;
            if (!locations.TryGetValue(balance.LocationId, out var location) || location is not { Active: true })
                continue;
            var available = balance.OnHand - balance.Allocated;
            if (available <= 0 || !balance.Sellable)
                continue;
            result.Add((new AllocationCandidate(balance.Id!, location.ToLocationRef(), balance.LotCode, balance.Expiry, balance.ReceivedAt, available), balance));
        }
        return result.Select(r => (r.Item1, r.Item2)).ToList();
    }

    /// <summary>The item's rule (<c>WarehouseItem.RemovalRule</c>), else <c>Spark:Warehouse:Allocation:Rule</c>.</summary>
    private IAllocationRule RuleFor(WarehouseItem item)
    {
        var name = item.RemovalRule ?? coreOptions.Value.Allocation.Rule;
        return rules.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? rules.First(r => string.Equals(r.Name, coreOptions.Value.Allocation.Rule, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Claims stock for one line in the rule's order. Each claim is one engine command (op id
    /// <c>{orderId}/alloc/{lineId}/{seq}</c>) that writes the order's allocation row in the same session; a stale candidate is
    /// skipped, never oversold (§4.5 "Indexes never decide").
    /// </summary>
    private async Task AllocateLineAsync(string orderId, string lineId, DateOnly today, IReadOnlyCollection<string>? excluded, string? userId, CancellationToken cancellationToken)
    {
        var order = (await LoadOrderAsync(orderId, cancellationToken))!;
        var line = order.Line(lineId)!;
        WarehouseItem? item;
        using (var session = store.OpenAsyncSession())
            item = await session.LoadAsync<WarehouseItem>(line.ItemId, cancellationToken);
        if (item is null)
            return;

        var candidates = await CandidatesAsync(order, line, today, excluded, cancellationToken);
        var request = new AllocationRequest(order.SiteId!, order.OrganizationId!, orderId, line.Number, line.ItemId, line.Unallocated,
            item.MinimumShelfLifeDays, today, line.RequiredLotCode);
        var ranked = await RuleFor(item).RankAsync(request, candidates.Select(c => c.Candidate).ToList(), cancellationToken);

        foreach (var candidate in ranked)
        {
            order = (await LoadOrderAsync(orderId, cancellationToken))!;
            line = order.Line(lineId)!;
            var need = line.Unallocated;
            if (need <= 0 || order.IsClosed || (order.CancellationRequested && order.CancellationAnswer is null))
                return;
            var quantity = Math.Min(need, candidate.Available);
            var seq = line.NextAllocationSeq;
            var op = Op($"{orderId}/alloc/{lineId}/{seq}", userId, orderId) with
            {
                InSession = WithOrder(orderId, (_, o) =>
                {
                    var l = o.Line(lineId);
                    if (l is null || l.NextAllocationSeq != seq || o.IsClosed || (o.CancellationRequested && o.CancellationAnswer is null))
                        return Task.FromResult<string?>("The order changed meanwhile.");
                    if (l.Unallocated < quantity)
                        return Task.FromResult<string?>("The line needs less now.");
                    l.Allocations.Add(new OutboundAllocation
                    {
                        Seq = seq,
                        BalanceId = candidate.BalanceId,
                        LocationId = candidate.Location.LocationId,
                        ZoneId = candidate.Location.ZoneId,
                        WalkSequence = candidate.Location.WalkSequence,
                        LotCode = candidate.LotCode,
                        Expiry = candidate.Expiry,
                        Quantity = quantity,
                    });
                    l.NextAllocationSeq = seq + 1;
                    return Task.FromResult<string?>(null);
                }),
            };
            var result = await stock.AllocateAsync(new AllocateStock(op, candidate.BalanceId, quantity), cancellationToken);
            if (result.Outcome == StockOutcome.Refused && result.Refusal is not (StockRefusal.InsufficientAvailable or StockRefusal.LotBlocked or StockRefusal.Declined))
                logger.LogWarning("Allocation of {OrderId} line {LineId} on {BalanceId} refused: {Refusal} {Detail}", orderId, lineId, candidate.BalanceId, result.Refusal, result.Detail);
        }
    }

    /// <summary>Releases the unpicked part of allocations (all lines, or one), with the order's rows in the same session.</summary>
    /// <param name="why">Part of the op ids (<c>{orderId}/release/{why}/{lineId}/{seq}/{remaining}</c>): one release per cause and state.</param>
    private async Task ReleaseOpenAllocationsAsync(string orderId, Func<OutboundAllocation, bool>? which, string why, CancellationToken cancellationToken,
        Action<OutboundOrder, OutboundLine, OutboundAllocation, decimal>? alsoInSession = null)
    {
        var order = await LoadOrderAsync(orderId, cancellationToken);
        if (order is null)
            return;
        foreach (var line in order.Lines)
        {
            foreach (var allocation in line.Allocations.Where(a => a.Quantity - a.Picked > 0 && (which is null || which(a))).ToList())
            {
                var open = allocation.Quantity - allocation.Picked;
                var lineId = line.LineId;
                var seq = allocation.Seq;
                var op = Op($"{orderId}/release/{why}/{lineId}/{seq}/{allocation.Quantity}", reference: orderId) with
                {
                    InSession = WithOrder(orderId, (_, o) =>
                    {
                        var l = o.Line(lineId);
                        var a = l?.Allocations.FirstOrDefault(x => x.Seq == seq);
                        if (l is null || a is null || a.Quantity - a.Picked != open)
                            return Task.FromResult<string?>("The allocation changed meanwhile.");
                        a.Quantity -= open;
                        alsoInSession?.Invoke(o, l, a, open);
                        if (a.Quantity == 0)
                            l.Allocations.Remove(a);
                        return Task.FromResult<string?>(null);
                    }),
                };
                await stock.AllocateAsync(new AllocateStock(op, allocation.BalanceId, -open), cancellationToken);
            }
        }
    }

    /// <summary>
    /// What no stock covers (§4.3 "Partial shipments and backorders"): <c>Allow</c> keeps it open as backordered (re-allocated
    /// when stock arrives); <c>CancelRemainder</c> reports it cancelled, and the shop refunds it (D45) and lowers <c>sold</c>.
    /// </summary>
    private async Task ApplyRemainderPolicyAsync(string orderId, CancellationToken cancellationToken, bool forceCancel = false)
    {
        var order = await LoadOrderAsync(orderId, cancellationToken);
        if (order is null || order.IsClosed)
            return;
        var cancel = forceCancel || order.BackorderPolicy == FulfilmentBackorderPolicy.CancelRemainder;
        if (cancel)
        {
            foreach (var line in order.Lines.Where(l => l.Unallocated > 0 || (forceCancel && l.Backordered > 0)))
                await CancelUnitsAsync(orderId, line.LineId, line.Unallocated + (forceCancel ? line.Backordered : 0), remainder: true, cancellationToken);
            return;
        }

        await ChangeOrderAsync(orderId, (_, o) =>
        {
            var changed = false;
            foreach (var line in o.Lines.Where(l => l.Unallocated > 0))
            {
                line.Backordered += line.Unallocated;
                changed = true;
            }
            return Task.FromResult((changed, changed));
        }, cancellationToken);
    }

    /// <summary>The released orders' allocations without a task yet, per site (also repairs a release a crash interrupted).</summary>
    private async Task<Dictionary<string, List<PickLine>>> UnplannedBySiteAsync(IEnumerable<string> orderIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, List<PickLine>>(StringComparer.OrdinalIgnoreCase);
        using var session = store.OpenAsyncSession();
        var orders = (await session.LoadAsync<OutboundOrder>(orderIds.Distinct(StringComparer.Ordinal), cancellationToken)).Values.OfType<OutboundOrder>()
            .OrderByDescending(o => o.Priority).ThenBy(o => o.PlacedAt).ToList();
        var locationIds = orders.SelectMany(o => o.Lines).SelectMany(l => l.Allocations).Select(a => a.LocationId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var locations = await session.LoadAsync<WarehouseLocation>(locationIds, cancellationToken);
        foreach (var order in orders.Where(o => !o.IsClosed && o.SiteId is not null))
        {
            foreach (var line in order.Lines)
            {
                // One pick line per (order, line, balance): the work maps it back to every unplanned allocation of that balance.
                foreach (var group in line.Allocations.Where(a => a.TaskId is null && a.Quantity - a.Picked > 0).GroupBy(a => a.BalanceId, StringComparer.Ordinal))
                {
                    var first = group.First();
                    if (!locations.TryGetValue(first.LocationId, out var location) || location is null)
                        continue;
                    if (!result.TryGetValue(order.SiteId!, out var lines))
                        result[order.SiteId!] = lines = [];
                    lines.Add(new PickLine(order.Id!, line.Number, line.ItemId, first.LotCode, first.BalanceId, location.ToLocationRef(), group.Sum(a => a.Quantity - a.Picked)));
                }
            }
        }
        return result;
    }

    /// <summary>The site's drop location for a sortation: put wall and zone → Consolidation; otherwise Staging, PackStation, Consolidation.</summary>
    private static async Task<WarehouseLocation?> DropLocationAsync(IAsyncDocumentSession session, string siteId, PickSortation sortation, CancellationToken cancellationToken)
    {
        WarehouseLocationType[] order = sortation is PickSortation.PutWall or PickSortation.Consolidation
            ? [WarehouseLocationType.Consolidation, WarehouseLocationType.PackStation, WarehouseLocationType.Staging]
            : [WarehouseLocationType.Staging, WarehouseLocationType.PackStation, WarehouseLocationType.Consolidation];
        var locations = await session.Query<WarehouseStockEntry>(WarehouseIndexes.Stock)
            .Where(e => e.Kind == WarehouseStockEntry.LocationKind && e.SiteId == siteId && e.Active
                && (e.Type == WarehouseLocationType.Staging || e.Type == WarehouseLocationType.PackStation || e.Type == WarehouseLocationType.Consolidation))
            .OrderBy(e => e.WalkSequence)
            .OfType<WarehouseLocation>()
            .Take(64)
            .ToListAsync(cancellationToken);
        foreach (var type in order)
        {
            if (locations.FirstOrDefault(l => l.Type == type) is { } found)
                return found;
        }
        return null;
    }
}
