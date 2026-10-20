using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Warehouse.Indexes;
using MintPlayer.Spark.Warehouse.Models;
using MintPlayer.Spark.Warehouse.Outbound.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Warehouse.Outbound.Services;

internal sealed partial class OutboundService
{
    internal const string DocumentKind = "Outbound";
    internal const string BackorderFlag = "Backorder";

    // --- consolidation (put wall, consolidation station; W4) ---------------------------------------------------------

    public async Task<SlotPlacement?> PutAsync(string siteId, string toteId, string itemId, decimal quantity, CancellationToken cancellationToken = default)
    {
        if (quantity <= 0)
            return null;
        var tote = WarehouseIds.Normalize(toteId);
        foreach (var orderId in await OrdersInToteAsync(siteId, tote, cancellationToken))
        {
            var placement = await PlaceAsync(orderId, tote, itemId, quantity, cancellationToken);
            if (placement is not null)
            {
                await ReleaseToteIfEmptyAsync(siteId, tote, cancellationToken);
                return placement;
            }
        }
        return null;
    }

    public async Task<IReadOnlyList<SlotPlacement>> ArriveToteAsync(string siteId, string toteId, CancellationToken cancellationToken = default)
    {
        var tote = WarehouseIds.Normalize(toteId);
        var placements = new List<SlotPlacement>();
        foreach (var orderId in await OrdersInToteAsync(siteId, tote, cancellationToken))
        {
            if (await PlaceAsync(orderId, tote, null, decimal.MaxValue, cancellationToken) is { } placement)
                placements.Add(placement);
        }
        await ReleaseToteIfEmptyAsync(siteId, tote, cancellationToken);
        return placements;
    }

    /// <summary>The orders with units in a tote, from the tote's claim (its batch), highest priority first.</summary>
    private async Task<List<string>> OrdersInToteAsync(string siteId, string tote, CancellationToken cancellationToken)
    {
        var claim = await claims.GetAsync(Totes, ToteKey(siteId, tote), cancellationToken);
        if (claim is null)
            return [];
        var owner = claim.Value.Owner;
        var hash = owner.IndexOf('#');
        if (hash > 0)
            return [owner[(hash + 1)..]];
        using var session = store.OpenAsyncSession();
        var orderIds = (await TasksOfAsync(session, owner, cancellationToken)).SelectMany(AllocationsOf).Select(r => r.OrderId).Distinct(StringComparer.Ordinal).ToList();
        var orders = await session.LoadAsync<OutboundOrder>(orderIds, cancellationToken);
        return orders.Values.OfType<OutboundOrder>().Where(o => !o.IsClosed)
            .OrderByDescending(o => o.Priority).ThenBy(o => o.PlacedAt).Select(o => o.Id!).ToList();
    }

    /// <summary>
    /// Puts units of one tote (one item, or all) into the order's slot: the slot is taken at the first arrival under
    /// optimistic concurrency, so two orders never get the same slot; the order is picked once every unit arrived.
    /// </summary>
    private Task<SlotPlacement?> PlaceAsync(string orderId, string tote, string? itemId, decimal quantity, CancellationToken cancellationToken)
        => OutboundSessions.RetryAsync<SlotPlacement?>(store, async session =>
        {
            var order = await session.LoadAsync<OutboundOrder>(orderId, cancellationToken);
            if (order is null || order.IsClosed || (order.CancellationRequested && order.CancellationAnswer is null))
                return (null, false);
            var picks = order.Lines.Where(l => itemId is null || l.ItemId == itemId)
                .SelectMany(l => l.Picks).Where(p => p.ToteId == tote && p.Quantity - p.PutBack - p.Arrived > 0).ToList();
            if (picks.Count == 0)
                return (null, false);

            var slot = await AssignSlotAsync(session, order, Now, cancellationToken);
            if (slot is null)
                return (null, false);
            var left = quantity;
            decimal placed = 0;
            foreach (var pick in picks)
            {
                var units = Math.Min(left, pick.Quantity - pick.PutBack - pick.Arrived);
                pick.Arrived += units;
                placed += units;
                left -= units;
                if (left <= 0)
                    break;
            }
            slot.Arrived += placed;
            if (!slot.Totes.Contains(tote))
                slot.Totes.Add(tote);
            Settle(order);
            order.UpdatedAt = Now;
            await OutboundStatus.WriteAsync(session, order, publisher, Now, cancellationToken);
            return (new SlotPlacement(orderId, slot.Id!, slot.Code, placed, OutboundReadiness.Complete(order)), true);
        }, cancellationToken);

    /// <summary>The order's slot, or the first free one of its site (null: the wall is full).</summary>
    internal static async Task<ConsolidationSlot?> AssignSlotAsync(IAsyncDocumentSession session, OutboundOrder order, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (order.ConsolidationSlotId is { } current && await session.LoadAsync<ConsolidationSlot>(current, cancellationToken) is { } held && held.OrderId == order.Id)
            return held;
        var slots = await session.Advanced.LoadStartingWithAsync<ConsolidationSlot>(ConsolidationSlot.PrefixFor(order.SiteId!), pageSize: 1024, token: cancellationToken);
        var free = slots.Where(s => s.Active && s.OrderId is null).OrderBy(s => s.Code, StringComparer.Ordinal).FirstOrDefault();
        if (free is null)
            return null;
        free.OrderId = order.Id;
        free.AssignedAt = now;
        free.Expected = order.Lines.Sum(l => l.Remaining);
        free.Arrived = 0;
        free.Totes = [];
        order.ConsolidationSlotId = free.Id;
        return free;
    }

    /// <summary>A tote whose units all arrived (or went back) is free for the next batch (X12 claim released).</summary>
    private async Task ReleaseToteIfEmptyAsync(string siteId, string tote, CancellationToken cancellationToken)
    {
        var claim = await claims.GetAsync(Totes, ToteKey(siteId, tote), cancellationToken);
        if (claim is null)
            return;
        var owner = claim.Value.Owner;
        foreach (var orderId in await OrdersInToteAsync(siteId, tote, cancellationToken))
        {
            var order = await LoadOrderAsync(orderId, cancellationToken);
            if (order?.Lines.SelectMany(l => l.Picks).Any(p => p.ToteId == tote && p.Quantity - p.PutBack - p.Arrived > 0 && p.Quantity - p.PutBack - p.Shipped > 0) == true)
                return;
        }
        await claims.ReleaseAsync(Totes, ToteKey(siteId, tote), owner, cancellationToken);
    }

    // --- backorders --------------------------------------------------------------------------------------------------

    public async Task<int> RetryAsync(string? siteId = null, string? itemId = null, CancellationToken cancellationToken = default)
    {
        List<OutboundOrder> orders;
        using (var session = store.OpenAsyncSession())
        {
            var query = session.Query<WarehouseDocumentEntry>(WarehouseIndexes.Documents)
                .Where(e => e.Kind == DocumentKind && e.Flag == BackorderFlag);
            if (siteId is not null)
                query = query.Where(e => e.SiteId == siteId);
            orders = await query.OfType<OutboundOrder>().Take(1024).ToListAsync(cancellationToken);
        }

        var released = 0;
        foreach (var order in orders.OrderByDescending(o => o.Priority).ThenBy(o => o.PlacedAt))
        {
            var lines = order.Lines.Where(l => l.Backordered > 0 && (itemId is null || l.ItemId == itemId)).ToList();
            if (lines.Count == 0 || order.IsClosed)
                continue;
            DateOnly today;
            using (var session = store.OpenAsyncSession())
                today = await SiteTodayAsync(session, order.SiteId, cancellationToken);
            var stocked = new List<string>();
            foreach (var line in lines)
            {
                if ((await CandidatesAsync(order, line, today, null, cancellationToken)).Sum(c => c.Candidate.Available) > 0)
                    stocked.Add(line.LineId);
            }
            if (stocked.Count == 0)
                continue;
            // Back to "unallocated"; the release allocates what it can and backorders the rest again.
            await ChangeOrderAsync(order.Id!, (_, o) =>
            {
                foreach (var lineId in stocked)
                    if (o.Line(lineId) is { } line)
                        line.Backordered = 0;
                return Task.FromResult((true, true));
            }, cancellationToken);
            var report = await ReleaseAsync([order.Id!], cancellationToken: cancellationToken);
            if (report.Released.Count > 0)
                released++;
        }
        return released;
    }

    // --- waves -------------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<string>> RunDueAsync(CancellationToken cancellationToken = default)
    {
        var run = new List<string>();
        var now = Now;
        foreach (var (siteCode, cutOffs) in Options.Release.Waves)
        {
            var code = WarehouseIds.Normalize(siteCode);
            var siteId = WarehouseSite.IdFor(code);
            var scheduleId = OutboundWaveSchedule.IdPrefix + code;
            string? waveId = null;
            bool claimed;
            try
            {
            claimed = await OutboundSessions.RetryAsync(store, async session =>
            {
                var site = await session.LoadAsync<WarehouseSite>(siteId, cancellationToken);
                if (site is not { Active: true })
                    return (false, false);
                var schedule = await session.LoadAsync<OutboundWaveSchedule>(scheduleId, cancellationToken);
                if (schedule is null)
                {
                    // First run: start from now; cut-offs before the schedule existed are not released retroactively.
                    await session.StoreAsync(new OutboundWaveSchedule { Id = scheduleId, LastCutOff = now }, string.Empty, scheduleId, cancellationToken);
                    return (false, true);
                }
                var zone = SparkTimeZones.FindOrUtc(site.TimeZone);
                DateTimeOffset? due = null;
                foreach (var cron in cutOffs)
                {
                    if (WaveSchedule.Parse(cron) is not { } parsed)
                        continue;
                    var next = WaveSchedule.Next(parsed, zone, schedule.LastCutOff);
                    for (var guard = 0; next is { } n && n <= now && guard < 1000; guard++)
                    {
                        if (due is null || n > due)
                            due = n;
                        next = WaveSchedule.Next(parsed, zone, n);
                    }
                }
                if (due is not { } cutOff)
                    return (false, false);
                schedule.LastCutOff = cutOff;
                waveId = $"{OutboundWave.IdPrefix}{code}/{cutOff.UtcDateTime:yyyyMMddHHmm}";
                await session.StoreAsync(new OutboundWave { Id = waveId, SiteId = siteId, CutOff = cutOff, StartedAt = now }, string.Empty, waveId, cancellationToken);
                return (true, true);
            }, cancellationToken, attempts: 1);
            }
            catch (Raven.Client.Exceptions.ConcurrencyException)
            {
                // Another node took this cut-off (the wave's natural id, or the schedule, changed meanwhile).
                claimed = false;
            }
            if (!claimed || waveId is null)
                continue;

            var report = await ReleaseSiteAsync(siteId, waveId, cancellationToken);
            await OutboundSessions.RetryAsync(store, async session =>
            {
                var wave = await session.LoadAsync<OutboundWave>(waveId, cancellationToken);
                if (wave is null)
                    return (false, false);
                wave.Released = report.Released.Count;
                wave.Waiting = report.Waiting.Count;
                wave.CompletedAt = Now;
                return (true, true);
            }, cancellationToken);
            logger.LogInformation("Wave {WaveId}: {Released} released, {Waiting} waiting.", waveId, report.Released.Count, report.Waiting.Count);
            run.Add(waveId);
        }
        return run;
    }

    public async Task<ReleaseReport> ReleaseSiteAsync(string siteId, string waveId, CancellationToken cancellationToken = default)
    {
        List<OutboundOrder> accepted;
        using (var session = store.OpenAsyncSession())
        {
            accepted = await session.Query<WarehouseDocumentEntry>(WarehouseIndexes.Documents)
                .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(30)))
                .Where(e => e.Kind == DocumentKind && e.SiteId == siteId && e.State == nameof(OutboundState.Accepted))
                .OrderBy(e => e.At)
                .OfType<OutboundOrder>()
                .Take(Options.Release.MaxOrdersPerWave)
                .ToListAsync(cancellationToken);
        }
        var ids = accepted.OrderByDescending(o => o.Priority).ThenBy(o => o.PlacedAt).Select(o => o.Id!).ToList();
        return await ReleaseAsync(ids, waveId, cancellationToken: cancellationToken);
    }

    // --- recall (round 5, GA7) ---------------------------------------------------------------------------------------

    /// <summary>
    /// A lot was recalled or quarantined (the core blocked it at once): the open allocations of pick tasks on it are released
    /// and the orders re-allocated from other lots, the rest backordered or cancelled per the shop's policy. Units already
    /// picked from the lot cannot ship (the engine refuses a recalled lot at ship-confirm); the packer puts them back.
    /// </summary>
    public async Task OnLotStatusChangedAsync(StockLot lot, CancellationToken cancellationToken)
    {
        if (lot.Status == StockLotStatus.Released)
            return;
        List<WarehouseTask> tasks;
        using (var session = store.OpenAsyncSession())
        {
            tasks = (await session.Query<WarehouseWorkEntry>(WarehouseIndexes.Work)
                .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(30)))
                .Where(t => t.Kind == WarehouseTaskKind.Pick && t.ItemId == lot.ItemId
                    && (t.State == WarehouseTaskState.Open || t.State == WarehouseTaskState.Assigned || t.State == WarehouseTaskState.InProgress))
                .OfType<WarehouseTask>()
                .Take(4096)
                .ToListAsync(cancellationToken))
                .Where(t => t.LotCode == lot.LotCode)
                .ToList();
        }

        var affected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            foreach (var reference in AllocationsOf(task))
            {
                affected.Add(reference.OrderId);
                await ReleaseOpenAllocationsAsync(reference.OrderId, a => a.Seq == reference.Seq && a.TaskId == task.Id, $"recall-{lot.LotCode}", cancellationToken);
            }
            await OutboundSessions.RetryAsync(store, async session =>
            {
                var t = await session.LoadAsync<WarehouseTask>(task.Id!, cancellationToken);
                if (t is null || t.State is WarehouseTaskState.Done or WarehouseTaskState.Cancelled)
                    return (false, false);
                t.Quantity = t.Confirmed;
                t.State = t.Confirmed > 0 ? WarehouseTaskState.Done : WarehouseTaskState.Cancelled;
                t.CompletedAt = Now;
                t.Payload["recalled"] = lot.LotCode;
                return (true, true);
            }, cancellationToken);
        }

        foreach (var orderId in affected)
        {
            var order = await LoadOrderAsync(orderId, cancellationToken);
            if (order is null || order.IsClosed)
                continue;
            DateOnly today;
            using (var session = store.OpenAsyncSession())
                today = await SiteTodayAsync(session, order.SiteId, cancellationToken);
            foreach (var line in order.Lines.Where(l => l.Unallocated > 0))
                await AllocateLineAsync(orderId, line.LineId, today, null, null, cancellationToken);
            await ApplyRemainderPolicyAsync(orderId, cancellationToken);
            foreach (var site in await UnplannedBySiteAsync([orderId], cancellationToken))
                await PlanWorkAsync(site.Key, site.Value, null, WarehouseStrategyNames.Discrete, cancellationToken);
        }
        logger.LogInformation("Lot {LotId} {Status}: {Tasks} pick task(s) withdrawn, {Orders} order(s) re-allocated.", lot.Id, lot.Status, tasks.Count, affected.Count);
    }
}
