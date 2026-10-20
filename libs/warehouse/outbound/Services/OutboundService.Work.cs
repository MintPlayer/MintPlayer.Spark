using MintPlayer.Spark.Warehouse.Models;
using MintPlayer.Spark.Warehouse.Outbound.Models;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Warehouse.Outbound.Services;

internal sealed partial class OutboundService
{
    private IPickStrategy StrategyFor(string? name)
    {
        name ??= Options.Picking.Strategy;
        return strategies.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No pick strategy named '{name}' is registered.");
    }

    private IPickRouter Router => routers.FirstOrDefault(r => string.Equals(r.Name, Options.Picking.Router, StringComparison.OrdinalIgnoreCase))
        ?? routers.First();

    /// <summary>
    /// Turns released lines into pick work (PRD §4.7 Pick work): the strategy groups them into trips (one
    /// <c>WarehouseTaskGroup</c> each), the router orders each trip's visits by walk sequence, and every visit becomes one
    /// pick task (location, item, lot) carrying the allocations it serves. Each trip commits in one session with the orders'
    /// allocation rows (task ids), so a plan is never half written. Null when the site has no drop location.
    /// </summary>
    private async Task<List<string>?> PlanWorkAsync(string siteId, IReadOnlyList<PickLine> lines, string? waveId, string? strategyName, CancellationToken cancellationToken)
    {
        if (lines.Count == 0)
            return [];
        var strategy = StrategyFor(strategyName);

        // Q-W7: batches may mix owners unless switched off; then each owner is planned on its own.
        var portions = new List<IReadOnlyList<PickLine>>();
        if (Options.Picking.MixOwners)
            portions.Add(lines);
        else
        {
            using var session = store.OpenAsyncSession();
            var owners = (await session.LoadAsync<OutboundOrder>(lines.Select(l => l.OrderId).Distinct(), cancellationToken))
                .ToDictionary(p => p.Key, p => p.Value?.OrganizationId ?? "", StringComparer.Ordinal);
            portions.AddRange(lines.GroupBy(l => owners.GetValueOrDefault(l.OrderId, "")).Select(g => (IReadOnlyList<PickLine>)g.ToList()));
        }

        var groups = new List<string>();
        foreach (var portion in portions)
        {
            var plan = await strategy.PlanAsync(new PickPlanRequest(siteId, waveId, portion), cancellationToken);
            foreach (var trip in plan.Trips.Where(t => t.Lines.Count > 0))
            {
                var groupId = await WriteTripAsync(siteId, trip, cancellationToken);
                if (groupId is null)
                    return null;
                if (groupId.Length > 0)
                    groups.Add(groupId);
            }
        }
        return groups;
    }

    /// <summary>One trip: its group, its tasks in route order, and the orders' task ids. "" when nothing was left to plan.</summary>
    private async Task<string?> WriteTripAsync(string siteId, PickTrip trip, CancellationToken cancellationToken)
    {
        var key = Guid.NewGuid().ToString("N");
        var groupId = WarehouseTaskGroup.IdPrefix + key;
        var locationRefs = trip.Lines.Select(l => l.Location).ToList();
        WarehouseLocation? drop;
        using (var session = store.OpenAsyncSession())
            drop = await DropLocationAsync(session, siteId, trip.Sortation, cancellationToken);
        if (drop is null)
            return null;
        var route = await Router.SequenceAsync(new PickRouteRequest(siteId, trip.ZoneId, null, drop.ToLocationRef(), locationRefs), cancellationToken);
        var rank = route.Select((l, i) => (l.LocationId, i)).ToDictionary(x => x.LocationId, x => x.i, StringComparer.OrdinalIgnoreCase);

        return await OutboundSessions.RetryAsync(store, async session =>
        {
            var now = Now;
            var orders = (await session.LoadAsync<OutboundOrder>(trip.OrderIds, cancellationToken)).Values.OfType<OutboundOrder>()
                .ToDictionary(o => o.Id!, StringComparer.Ordinal);
            var visits = trip.Lines
                .GroupBy(l => (l.Location.LocationId, l.ItemId, l.LotCode))
                .OrderBy(g => rank.GetValueOrDefault(g.Key.LocationId, int.MaxValue))
                .ThenBy(g => g.Key.ItemId, StringComparer.Ordinal)
                .ToList();

            var number = 0;
            foreach (var visit in visits)
            {
                var taskId = $"{WarehouseTask.IdPrefix}{key}/{++number:D4}";
                var refs = new List<TaskAllocationRef>();
                var owners = new HashSet<string>(StringComparer.Ordinal);
                var priority = 0;
                foreach (var pickLine in visit)
                {
                    if (!orders.TryGetValue(pickLine.OrderId, out var order) || order.IsClosed)
                        continue;
                    var line = order.Lines.FirstOrDefault(l => l.Number == pickLine.LineNumber);
                    if (line is null)
                        continue;
                    foreach (var allocation in line.Allocations.Where(a => a.TaskId is null && a.BalanceId == pickLine.BalanceId && a.Quantity - a.Picked > 0))
                    {
                        allocation.TaskId = taskId;
                        refs.Add(new TaskAllocationRef(order.Id!, line.LineId, allocation.Seq, allocation.Quantity - allocation.Picked));
                    }
                    if (order.OrganizationId is not null)
                        owners.Add(order.OrganizationId);
                    priority = Math.Max(priority, order.Priority);
                }
                if (refs.Count == 0)
                {
                    number--;
                    continue;
                }

                var location = visit.First().Location;
                var task = new WarehouseTask
                {
                    Id = taskId,
                    Kind = WarehouseTaskKind.Pick,
                    State = WarehouseTaskState.Open,
                    SiteId = siteId,
                    ZoneId = location.ZoneId,
                    LocationId = location.LocationId,
                    WalkSequence = location.WalkSequence,
                    TargetLocationId = drop.Id,
                    ItemId = visit.Key.ItemId,
                    LotCode = visit.Key.LotCode,
                    Quantity = refs.Sum(r => r.Quantity),
                    OrganizationIds = [.. owners],
                    GroupId = groupId,
                    Reference = OutboundReference,
                    Priority = priority,
                    CreatedAt = now,
                };
                SetAllocations(task, refs);
                task.Payload[PayloadSortation] = trip.Sortation.ToString();
                await session.StoreAsync(task, string.Empty, taskId, cancellationToken);
            }
            if (number == 0)
                return ("", false);

            await session.StoreAsync(new WarehouseTaskGroup
            {
                Id = groupId,
                Kind = WarehouseTaskGroupKind.PickBatch,
                SiteId = siteId,
                State = WarehouseTaskState.Open,
                CreatedAt = now,
            }, string.Empty, groupId, cancellationToken);

            foreach (var order in orders.Values.Where(o => o.Lines.Any(l => l.Allocations.Any(a => a.TaskId?.StartsWith(TaskPrefix(groupId), StringComparison.Ordinal) == true))))
            {
                order.GroupIds.Add(groupId);
                // Once an order sorts into a slot it keeps doing so; units picked by a trip that needs no slot count as
                // arrived when picked (ConfirmAsync), so a re-allocated line never waits for a slot it will not reach.
                if (!OutboundReadiness.NeedsSlot(order))
                    order.Sortation = trip.Sortation;
                if (order.State == OutboundState.Accepted)
                    order.State = OutboundState.Released;
                order.UpdatedAt = now;
                await OutboundStatus.WriteAsync(session, order, publisher, now, cancellationToken);
            }
            return (groupId, true);
        }, cancellationToken);
    }
}
