using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Claims;
using MintPlayer.Spark.Warehouse.Models;
using MintPlayer.Spark.Warehouse.Outbound.Models;
using MintPlayer.Spark.Warehouse.Stock;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Warehouse.Outbound.Services;

internal sealed partial class OutboundService
{
    private const int RememberedOps = 64;

    // --- batch leases ------------------------------------------------------------------------------------------------

    public Task<BatchClaimOutcome> ClaimBatchAsync(string groupId, string userId, CancellationToken cancellationToken = default)
        => LeaseBatchAsync(groupId, userId, takeOver: false, force: false, cancellationToken);

    public Task<BatchClaimOutcome> TakeOverBatchAsync(string groupId, string userId, bool force, CancellationToken cancellationToken = default)
        => LeaseBatchAsync(groupId, userId, takeOver: true, force, cancellationToken);

    /// <summary>
    /// A picker holds a batch under a lease (PRD §4.7; the lease lives on the group and its tasks under optimistic concurrency:
    /// two claims at once conflict, one wins). A supervisor takes it over once the lease lapsed, or at once with <paramref name="force"/>.
    /// </summary>
    private async Task<BatchClaimOutcome> LeaseBatchAsync(string groupId, string userId, bool takeOver, bool force, CancellationToken cancellationToken)
    {
        using var session = OutboundSessions.Open(store);
        var group = await session.LoadAsync<WarehouseTaskGroup>(groupId, cancellationToken);
        if (group is null)
            return BatchClaimOutcome.Unknown;
        if (group.State is WarehouseTaskState.Done or WarehouseTaskState.Cancelled)
            return BatchClaimOutcome.NotOpen;
        var now = Now;
        var renewed = group.AssignedTo == userId;
        var live = group.AssignedTo is not null && group.LeaseUntil > now;
        if (!renewed && live && !(takeOver && force))
            return BatchClaimOutcome.HeldByOther;

        var until = now + coreOptions.Value.Tasks.Lease;
        group.AssignedTo = userId;
        group.LeaseUntil = until;
        if (group.State == WarehouseTaskState.Open)
            group.State = WarehouseTaskState.Assigned;
        foreach (var task in await TasksOfAsync(session, groupId, cancellationToken))
        {
            if (task.State is WarehouseTaskState.Done or WarehouseTaskState.Cancelled)
                continue;
            task.AssignedTo = userId;
            task.LeaseUntil = until;
            if (task.State == WarehouseTaskState.Open)
                task.State = WarehouseTaskState.Assigned;
        }
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Raven.Client.Exceptions.ConcurrencyException)
        {
            return BatchClaimOutcome.HeldByOther;
        }
        if (takeOver && !renewed)
            logger.LogInformation("Pick batch {GroupId} taken over by {UserId}{Force}.", groupId, userId, force ? " (forced)" : "");
        return renewed ? BatchClaimOutcome.Renewed : BatchClaimOutcome.Claimed;
    }

    // --- scan-to-confirm ---------------------------------------------------------------------------------------------

    internal static bool LocationMatches(WarehouseLocation location, string? scanned)
    {
        if (string.IsNullOrWhiteSpace(scanned))
            return false;
        var value = scanned.Trim();
        return string.Equals(value, location.Barcode, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, location.Code, StringComparison.OrdinalIgnoreCase)
            || (location.CheckDigits.Length > 0 && string.Equals(value, location.CheckDigits, StringComparison.Ordinal));
    }

    public async Task<PickResult> ConfirmAsync(PickConfirmation c, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentException.ThrowIfNullOrWhiteSpace(c.OpId);
        WarehouseTask? task;
        WarehouseLocation? location;
        Dictionary<string, StockMovement?> applied;
        using (var session = store.OpenAsyncSession())
        {
            task = await session.LoadAsync<WarehouseTask>(c.TaskId, cancellationToken);
            if (task is not { Kind: WarehouseTaskKind.Pick })
                return new PickResult(PickOutcome.Unknown);
            location = task.LocationId is null ? null : await session.LoadAsync<WarehouseLocation>(task.LocationId, cancellationToken);
            var all = AllocationsOf(task);
            applied = (await session.LoadAsync<StockMovement>(Enumerable.Range(0, all.Count).Select(i => StockMovement.IdFor($"{c.OpId}/{i}")), cancellationToken))
                .ToDictionary(p => p.Key, p => (StockMovement?)p.Value, StringComparer.OrdinalIgnoreCase);
        }

        var remaining = (task.Quantity ?? 0) - task.Confirmed;
        var alreadyApplied = applied.Values.OfType<StockMovement>().Sum(m => m.Quantity);
        if (task.State is WarehouseTaskState.Done or WarehouseTaskState.Cancelled)
            return new PickResult(alreadyApplied > 0 ? PickOutcome.Replayed : PickOutcome.NotOpen, remaining);
        if (task.AssignedTo != c.UserId || task.LeaseUntil is not { } lease || lease <= Now)
            return new PickResult(PickOutcome.NotHolder, remaining);
        if (location is null)
            return new PickResult(PickOutcome.Unknown, remaining);

        var mode = Options.Picking.Confirm;
        if (mode == PickConfirmMode.ScanLocationAndItem && !LocationMatches(location, c.ScannedLocation))
            return new PickResult(PickOutcome.WrongLocation, remaining);

        decimal packQuantity = 1;
        string? scannedLot = null;
        if (mode != PickConfirmMode.Manual)
        {
            if (c.ItemBarcode is { Length: > 0 } barcode)
            {
                // The barcode pipeline with the task's item pre-selected (§4.6); other owners' items are filtered out.
                var resolution = await barcodes.ResolveAsync(new BarcodeScanContext(barcode, task.SiteId, task.OrganizationIds, task.ItemId, task.Id), false, cancellationToken);
                var match = resolution.Preselected is { Kind: BarcodeTargetKind.Item } pre && pre.TargetId == task.ItemId
                    ? pre
                    : resolution.Matches.FirstOrDefault(m => m.Kind == BarcodeTargetKind.Item && m.TargetId == task.ItemId);
                if (match is null)
                    return new PickResult(PickOutcome.WrongItem, remaining);
                packQuantity = match.PackQuantity;
                scannedLot = resolution.Scan.LotCode;
            }
            else if (c.ItemId != task.ItemId)
                return new PickResult(PickOutcome.WrongItem, remaining);
        }

        var lot = c.LotCode ?? scannedLot;
        if (task.LotCode is not null && Options.Picking.LotCheck && !string.Equals(lot, task.LotCode, StringComparison.Ordinal))
        {
            if (lot is null || !Options.Picking.LotSubstitution)
                return new PickResult(PickOutcome.WrongLot, remaining, task.LotCode);
            if (await SubstituteLotAsync(task, lot, c.UserId, cancellationToken) is { } problem)
                return new PickResult(PickOutcome.WrongLot, remaining, problem);
            using var session = store.OpenAsyncSession();
            task = (await session.LoadAsync<WarehouseTask>(c.TaskId, cancellationToken))!;
        }

        var quantity = c.Quantity ?? packQuantity;
        if (quantity <= 0)
            return new PickResult(PickOutcome.TooMany, remaining, "A pick needs a positive quantity.");
        if (alreadyApplied >= quantity)
            return new PickResult(PickOutcome.Replayed, remaining);
        var toApply = quantity - alreadyApplied;
        if (toApply > remaining)
            return new PickResult(PickOutcome.TooMany, remaining);

        var refs = AllocationsOf(task);
        var sortation = SortationOf(task);
        if (sortation != PickSortation.None)
        {
            if (string.IsNullOrWhiteSpace(c.ToteId))
                return new PickResult(PickOutcome.ToteRequired, remaining);
            var orders = refs.Select(r => r.OrderId).Distinct(StringComparer.Ordinal).ToList();
            if (sortation == PickSortation.ToteOnCart && c.OrderId is null && orders.Count > 1)
                return new PickResult(PickOutcome.ToteRequired, remaining, "Cluster picking: say which order's tote the units went into.");
            var owner = sortation == PickSortation.ToteOnCart ? $"{task.GroupId}#{c.OrderId ?? orders.FirstOrDefault()}" : task.GroupId!;
            var tote = await claims.ReserveHoldAsync(Totes, ToteKey(task.SiteId, c.ToteId), owner, null, cancellationToken: cancellationToken);
            if (!tote.Holds)
                return new PickResult(PickOutcome.ToteInUse, remaining);
        }

        // Spread the units over the task's allocations in payload order; portion i has op id {opId}/{i}, so a re-sent
        // confirmation replays what was applied and applies only the rest.
        var needsSlot = sortation is PickSortation.PutWall or PickSortation.Consolidation;
        for (var i = 0; i < refs.Count && toApply > 0; i++)
        {
            var reference = refs[i];
            if (c.OrderId is not null && reference.OrderId != c.OrderId)
                continue;
            if (applied.GetValueOrDefault(StockMovement.IdFor($"{c.OpId}/{i}")) is not null)
                continue;
            var order = await LoadOrderAsync(reference.OrderId, cancellationToken);
            var allocation = order?.Line(reference.LineId)?.Allocations.FirstOrDefault(a => a.Seq == reference.Seq);
            if (order is null || allocation is null)
                continue;
            var open = allocation.Quantity - allocation.Picked;
            if (open <= 0)
                continue;
            var portion = Math.Min(open, toApply);
            var op = Op($"{c.OpId}/{i}", c.UserId, task.Id, c.DeviceId, c.DeviceTime) with
            {
                InSession = PickInSession(task.Id!, reference, portion, $"{c.OpId}/{i}", c.ToteId, task.ZoneId, task.TargetLocationId!, needsSlot, c.UserId),
            };
            var result = await stock.PickAsync(new MoveStock(op, allocation.BalanceId, task.TargetLocationId!, portion, CarryAllocated: portion), cancellationToken);
            if (!result.Succeeded)
                return new PickResult(PickOutcome.Refused, remaining, $"{result.Refusal}: {result.Detail}");
            toApply -= portion;
        }

        using (var session = store.OpenAsyncSession())
        {
            var after = await session.LoadAsync<WarehouseTask>(c.TaskId, cancellationToken);
            return new PickResult(PickOutcome.Confirmed, (after?.Quantity ?? 0) - (after?.Confirmed ?? 0));
        }
    }

    internal static SparkClaimKey ToteKey(string siteId, string toteId) => Totes.Key($"{siteId}/{WarehouseIds.Normalize(toteId)}");

    /// <summary>The order's pick record and the task's progress, in the pick's own session (one commit with the stock move).</summary>
    private Func<IAsyncDocumentSession, CancellationToken, Task<string?>> PickInSession(string taskId, TaskAllocationRef reference, decimal quantity,
        string opId, string? toteId, string? zoneId, string dropLocationId, bool needsSlot, string userId)
        => async (session, cancellationToken) =>
        {
            var now = Now;
            var task = await session.LoadAsync<WarehouseTask>(taskId, cancellationToken);
            if (task is null || task.State is WarehouseTaskState.Done or WarehouseTaskState.Cancelled)
                return "The task is no longer open.";
            if (task.AssignedTo != userId)
                return "The task is no longer yours.";

            var declined = await WithOrder(reference.OrderId, (_, order) =>
            {
                var line = order.Line(reference.LineId);
                var allocation = line?.Allocations.FirstOrDefault(a => a.Seq == reference.Seq);
                if (line is null || allocation is null || allocation.Quantity - allocation.Picked < quantity)
                    return Task.FromResult<string?>("The allocation changed meanwhile.");
                if (order.CancellationRequested && order.CancellationAnswer is null)
                    return Task.FromResult<string?>("The order is being cancelled.");
                allocation.Picked += quantity;
                line.Picks.Add(new OutboundPick
                {
                    OpId = opId,
                    BalanceId = StockBalance.IdFor(dropLocationId, line.ItemId, allocation.LotCode),
                    LocationId = dropLocationId,
                    FromLocationId = allocation.LocationId,
                    LotCode = allocation.LotCode,
                    Expiry = allocation.Expiry,
                    Quantity = quantity,
                    ToteId = toteId is null ? null : WarehouseIds.Normalize(toteId),
                    ZoneId = zoneId,
                    TaskId = taskId,
                    // Units that need no slot are where packing takes them as soon as they are picked.
                    Arrived = needsSlot ? 0 : quantity,
                });
                Settle(order);
                return Task.FromResult<string?>(null);
            })(session, cancellationToken);
            if (declined is not null)
                return declined;

            task.Confirmed += quantity;
            task.ConfirmedOpIds.Add(opId);
            if (task.ConfirmedOpIds.Count > RememberedOps)
                task.ConfirmedOpIds.RemoveRange(0, task.ConfirmedOpIds.Count - RememberedOps);
            task.StartedAt ??= now;
            task.LeaseUntil = now + coreOptions.Value.Tasks.Lease;
            task.State = WarehouseTaskState.InProgress;
            if (task.Confirmed >= (task.Quantity ?? 0))
                await FinishTaskAsync(session, task, userId, now, cancellationToken);

            if (task.GroupId is not null && await session.LoadAsync<WarehouseTaskGroup>(task.GroupId, cancellationToken) is { } group)
            {
                group.LeaseUntil = now + coreOptions.Value.Tasks.Lease;
                group.State = WarehouseTaskState.InProgress;
                if (task.State == WarehouseTaskState.Done
                    && (await TasksOfAsync(session, task.GroupId, cancellationToken)).All(t => t.Id == task.Id || t.State is WarehouseTaskState.Done or WarehouseTaskState.Cancelled))
                    group.State = WarehouseTaskState.Done;
            }
            return null;
        };

    /// <summary>Marks a task done and records its productivity event (Q-W19), in the caller's session.</summary>
    private static async Task FinishTaskAsync(IAsyncDocumentSession session, WarehouseTask task, string? userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        task.State = WarehouseTaskState.Done;
        task.CompletedAt = now;
        if (userId is null)
            return;
        var eventId = ProductivityEvent.IdPrefix + Uri.EscapeDataString(task.Id!);
        if (await session.LoadAsync<ProductivityEvent>(eventId, cancellationToken) is not null)
            return;
        await session.StoreAsync(new ProductivityEvent
        {
            Id = eventId,
            Kind = task.Kind,
            UserId = userId,
            SiteId = task.SiteId,
            TaskId = task.Id,
            Quantity = task.Confirmed,
            Lines = Math.Max(1, AllocationsOf(task).Count),
            StartedAt = task.StartedAt ?? now,
            EndedAt = now,
        }, eventId, cancellationToken);
    }

    /// <summary>
    /// Q-W6 lot substitution (a switch, default off): the task's open allocations move to the scanned lot when that lot is in
    /// the same location, released, unallocated enough, and meets the item's minimum shelf life. The swap is two ledger
    /// entries (claim the new balance with the order's row in its session, then release the old one). Null on success.
    /// </summary>
    private async Task<string?> SubstituteLotAsync(WarehouseTask task, string lotCode, string userId, CancellationToken cancellationToken)
    {
        var newBalanceId = StockBalance.IdFor(task.LocationId!, task.ItemId!, lotCode);
        using (var session = store.OpenAsyncSession())
        {
            var balance = await session.LoadAsync<StockBalance>(newBalanceId, cancellationToken);
            var lot = await session.LoadAsync<StockLot>(StockLot.IdFor(task.ItemId!, lotCode), cancellationToken);
            var item = await session.LoadAsync<WarehouseItem>(task.ItemId!, cancellationToken);
            var today = await SiteTodayAsync(session, task.SiteId, cancellationToken);
            var open = (task.Quantity ?? 0) - task.Confirmed;
            if (balance is null || lot is not { Status: StockLotStatus.Released } || !balance.Sellable)
                return $"Lot {lotCode} is not released stock at this location.";
            if (balance.OnHand - balance.Allocated < open)
                return $"Lot {lotCode} has {balance.OnHand - balance.Allocated} unallocated here.";
            if (lot.Expiry is { } expiry && expiry.AddDays(-(item?.MinimumShelfLifeDays ?? 0)) < today)
                return $"Lot {lotCode} is inside the minimum shelf life.";
        }

        foreach (var reference in AllocationsOf(task))
        {
            var order = await LoadOrderAsync(reference.OrderId, cancellationToken);
            var allocation = order?.Line(reference.LineId)?.Allocations.FirstOrDefault(a => a.Seq == reference.Seq);
            if (order is null || allocation is null || allocation.Quantity - allocation.Picked <= 0 || allocation.LotCode == lotCode)
                continue;
            var open = allocation.Quantity - allocation.Picked;
            var oldBalanceId = allocation.BalanceId;
            var newSeq = order.Line(reference.LineId)!.NextAllocationSeq;
            var baseOp = $"{task.Id}/substitute/{reference.OrderId}/{reference.LineId}/{reference.Seq}";
            var claim = Op(baseOp + "/claim", userId, task.Id) with
            {
                InSession = async (session, ct) =>
                {
                    var declined = await WithOrder(reference.OrderId, (_, o) =>
                    {
                        var l = o.Line(reference.LineId);
                        var a = l?.Allocations.FirstOrDefault(x => x.Seq == reference.Seq);
                        if (l is null || a is null || a.Quantity - a.Picked != open || l.NextAllocationSeq != newSeq)
                            return Task.FromResult<string?>("The allocation changed meanwhile.");
                        a.Quantity -= open;
                        if (a.Quantity == 0)
                            l.Allocations.Remove(a);
                        l.Allocations.Add(new OutboundAllocation
                        {
                            Seq = newSeq,
                            BalanceId = newBalanceId,
                            LocationId = a.LocationId,
                            ZoneId = a.ZoneId,
                            WalkSequence = a.WalkSequence,
                            LotCode = lotCode,
                            Quantity = open,
                            TaskId = task.Id,
                        });
                        l.NextAllocationSeq = newSeq + 1;
                        return Task.FromResult<string?>(null);
                    })(session, ct);
                    if (declined is not null)
                        return declined;
                    var t = await session.LoadAsync<WarehouseTask>(task.Id!, ct);
                    if (t is null)
                        return "The task is gone.";
                    SetAllocations(t, AllocationsOf(t).Select(r => r.OrderId == reference.OrderId && r.LineId == reference.LineId && r.Seq == reference.Seq
                        ? r with { Seq = newSeq, Quantity = open } : r));
                    t.LotCode = lotCode;
                    return null;
                },
            };
            var claimed = await stock.AllocateAsync(new AllocateStock(claim, newBalanceId, open), cancellationToken);
            if (!claimed.Succeeded)
                return $"{claimed.Refusal}: {claimed.Detail}";
            await stock.AllocateAsync(new AllocateStock(Op(baseOp + "/release", userId, task.Id), oldBalanceId, -open), cancellationToken);
        }
        return null;
    }

    // --- short picks -------------------------------------------------------------------------------------------------

    public async Task<PickResult> ShortPickAsync(string taskId, string userId, string opId, string? reasonCode = null, CancellationToken cancellationToken = default)
    {
        WarehouseTask? task;
        using (var session = store.OpenAsyncSession())
            task = await session.LoadAsync<WarehouseTask>(taskId, cancellationToken);
        if (task is not { Kind: WarehouseTaskKind.Pick })
            return new PickResult(PickOutcome.Unknown);
        if (task.State is WarehouseTaskState.Done or WarehouseTaskState.Cancelled)
            return new PickResult(task.ConfirmedOpIds.Contains(opId) ? PickOutcome.Replayed : PickOutcome.NotOpen);
        if (task.AssignedTo != userId)
            return new PickResult(PickOutcome.NotHolder);

        // 1. The task's unpicked allocations are released; the lines remember the shortfall.
        var refs = AllocationsOf(task);
        var balances = new HashSet<string>(StringComparer.Ordinal);
        var affected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in refs)
        {
            affected.Add(reference.OrderId);
            await ReleaseOpenAllocationsAsync(reference.OrderId, a => a.Seq == reference.Seq && a.TaskId == taskId, "short",
                cancellationToken, (_, line, allocation, open) =>
                {
                    if (line.LineId == reference.LineId)
                        line.ShortPicked += open;
                });
            var order = await LoadOrderAsync(reference.OrderId, cancellationToken);
            if (order?.Line(reference.LineId)?.Allocations.FirstOrDefault(a => a.Seq == reference.Seq) is { } kept)
                balances.Add(kept.BalanceId);
        }
        balances.Add(StockBalance.IdFor(task.LocationId!, task.ItemId!, task.LotCode));

        // 2. The location's remaining (phantom) stock is zeroed with a reason; other orders' allocations on it stay and
        // short-pick on their own visit.
        var reason = reasonCode ?? Options.ShortPick.DefaultReasonCode;
        foreach (var balanceId in balances)
        {
            StockBalance? balance;
            using (var session = store.OpenAsyncSession())
                balance = await session.LoadAsync<StockBalance>(balanceId, cancellationToken);
            var phantom = balance is null ? 0 : balance.OnHand - balance.Allocated;
            if (balance is null || phantom <= 0)
                continue;
            var zeroed = await stock.AdjustAsync(new AdjustStock(Op($"{opId}/zero/{balanceId}", userId, taskId), balance.LocationId, balance.ItemId, balance.LotCode, -phantom, reason), cancellationToken);
            if (!zeroed.Succeeded)
                logger.LogWarning("Short pick {TaskId}: zeroing {BalanceId} refused ({Refusal}: {Detail}).", taskId, balanceId, zeroed.Refusal, zeroed.Detail);
        }

        // 3. The task ends with what was found.
        await OutboundSessions.RetryAsync(store, async session =>
        {
            var t = await session.LoadAsync<WarehouseTask>(taskId, cancellationToken);
            if (t is null || t.State is WarehouseTaskState.Done or WarehouseTaskState.Cancelled)
                return (false, false);
            t.Payload["short"] = ((t.Quantity ?? 0) - t.Confirmed).ToString(System.Globalization.CultureInfo.InvariantCulture);
            t.ConfirmedOpIds.Add(opId);
            await FinishTaskAsync(session, t, userId, Now, cancellationToken);
            return (true, true);
        }, cancellationToken);

        // 4. The orders: re-allocate, backorder, cancel or hold (Spark:Warehouse:ShortPick).
        foreach (var orderId in affected)
            await AfterShortAsync(orderId, balances, cancellationToken);

        // 5. A count of the location, and a top-up of a fixed pick face.
        if (Options.ShortPick.TriggerCycleCount)
            await TriggerCountAsync(task, cancellationToken);
        await replenishment.OnShortPickAsync(task.LocationId!, cancellationToken);
        return new PickResult(PickOutcome.Confirmed, 0);
    }

    /// <summary>What a shortfall does to the order (PRD §4.7 Short picks), then work for whatever was re-allocated.</summary>
    private async Task AfterShortAsync(string orderId, IReadOnlyCollection<string> excluded, CancellationToken cancellationToken)
    {
        switch (Options.ShortPick.Mode)
        {
            case ShortPickMode.HoldOrder:
                await HoldAsync(orderId, true, "short-pick", cancellationToken);
                return;
            case ShortPickMode.CancelRemainder:
                await ApplyRemainderPolicyAsync(orderId, cancellationToken, forceCancel: true);
                return;
            case ShortPickMode.Backorder:
                await ApplyRemainderPolicyAsync(orderId, cancellationToken);
                return;
            default:
                var order = await LoadOrderAsync(orderId, cancellationToken);
                if (order is null || order.IsClosed)
                    return;
                DateOnly today;
                using (var session = store.OpenAsyncSession())
                    today = await SiteTodayAsync(session, order.SiteId, cancellationToken);
                foreach (var line in order.Lines.Where(l => l.Unallocated > 0))
                    await AllocateLineAsync(orderId, line.LineId, today, excluded, null, cancellationToken);
                await ApplyRemainderPolicyAsync(orderId, cancellationToken);
                foreach (var site in await UnplannedBySiteAsync([orderId], cancellationToken))
                    await PlanWorkAsync(site.Key, site.Value, null, WarehouseStrategyNames.Discrete, cancellationToken);
                await ChangeOrderAsync(orderId, (_, o) =>
                {
                    var before = o.State;
                    Settle(o);
                    return Task.FromResult((true, o.State != before));
                }, cancellationToken);
                return;
        }
    }

    /// <summary>
    /// A blind count of the short-picked location (PRD §4.7: "optionally trigger a count"): one open Count task per location,
    /// at <c>warehouse-tasks/count/{site}/{location}</c>, for the Counting package (WM8) to plan and confirm.
    /// </summary>
    private async Task TriggerCountAsync(WarehouseTask task, CancellationToken cancellationToken)
    {
        var id = $"{WarehouseTask.IdPrefix}count/{Uri.EscapeDataString(task.LocationId!)}";
        using var session = OutboundSessions.Open(store);
        var existing = await session.LoadAsync<WarehouseTask>(id, cancellationToken);
        if (existing is { State: not (WarehouseTaskState.Done or WarehouseTaskState.Cancelled) })
            return;
        if (existing is not null)
            session.Advanced.Evict(existing);
        var count = new WarehouseTask
        {
            Id = id,
            Kind = WarehouseTaskKind.Count,
            State = WarehouseTaskState.Open,
            SiteId = task.SiteId,
            ZoneId = task.ZoneId,
            LocationId = task.LocationId,
            WalkSequence = task.WalkSequence,
            OrganizationIds = [.. task.OrganizationIds],
            Reference = "short-pick:" + task.Id,
            Priority = 10,
            CreatedAt = Now,
        };
        if (existing is null)
            await session.StoreAsync(count, string.Empty, id, cancellationToken);
        else
            await session.StoreAsync(count, id, cancellationToken);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Raven.Client.Exceptions.ConcurrencyException)
        {
            // Another short pick of the same location created it.
        }
    }

    // --- put-backs ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// A put-back (a cancellation after picking, PRD §4.3): the units' carried allocation is released, then they move from the
    /// drop location (or tote) back to where they were picked, with the order's pick record in the move's session.
    /// </summary>
    public async Task<PickResult> ConfirmPutBackAsync(string taskId, string userId, string opId, string? scannedLocation = null, CancellationToken cancellationToken = default)
    {
        WarehouseTask? task;
        WarehouseLocation? target;
        using (var session = store.OpenAsyncSession())
        {
            task = await session.LoadAsync<WarehouseTask>(taskId, cancellationToken);
            if (task is not { Kind: WarehouseTaskKind.PutBack })
                return new PickResult(PickOutcome.Unknown);
            target = task.TargetLocationId is null ? null : await session.LoadAsync<WarehouseLocation>(task.TargetLocationId, cancellationToken);
        }
        if (task.State is WarehouseTaskState.Done or WarehouseTaskState.Cancelled)
            return new PickResult(task.ConfirmedOpIds.Contains(opId) ? PickOutcome.Replayed : PickOutcome.NotOpen);
        if (task.AssignedTo is not null && task.AssignedTo != userId && task.LeaseUntil > Now)
            return new PickResult(PickOutcome.NotHolder);
        if (target is null)
            return new PickResult(PickOutcome.Unknown);
        if (Options.Picking.Confirm == PickConfirmMode.ScanLocationAndItem && !LocationMatches(target, scannedLocation))
            return new PickResult(PickOutcome.WrongLocation, task.Quantity ?? 0);

        var orderId = task.Payload.GetValueOrDefault(PayloadOrder) ?? "";
        var lineId = task.Payload.GetValueOrDefault(PayloadLine) ?? "";
        var pickOp = task.Payload.GetValueOrDefault(PayloadPick) ?? "";
        var quantity = (task.Quantity ?? 0) - task.Confirmed;
        var balanceId = StockBalance.IdFor(task.LocationId!, task.ItemId!, task.LotCode);

        var released = await stock.AllocateAsync(new AllocateStock(Op($"{opId}/release", userId, taskId), balanceId, -quantity), cancellationToken);
        if (!released.Succeeded)
            return new PickResult(PickOutcome.Refused, quantity, $"{released.Refusal}: {released.Detail}");

        var move = Op($"{opId}/move", userId, taskId) with
        {
            InSession = async (session, ct) =>
            {
                var t = await session.LoadAsync<WarehouseTask>(taskId, ct);
                if (t is null || t.State is WarehouseTaskState.Done or WarehouseTaskState.Cancelled)
                    return "The task is no longer open.";
                var declined = await WithOrder(orderId, (_, o) =>
                {
                    var pick = o.Line(lineId)?.Picks.FirstOrDefault(p => p.OpId == pickOp);
                    if (pick is null)
                        return Task.FromResult<string?>("The pick is gone.");
                    pick.PutBack += quantity;
                    return Task.FromResult<string?>(null);
                })(session, ct);
                if (declined is not null)
                    return declined;
                t.Confirmed += quantity;
                t.ConfirmedOpIds.Add(opId);
                t.AssignedTo ??= userId;
                await FinishTaskAsync(session, t, userId, Now, ct);
                return null;
            },
        };
        var moved = await stock.MoveAsync(new MoveStock(move, balanceId, target.Id!, quantity), cancellationToken);
        return moved.Succeeded
            ? new PickResult(moved.Outcome == StockOutcome.Replayed ? PickOutcome.Replayed : PickOutcome.Confirmed, 0)
            : new PickResult(PickOutcome.Refused, quantity, $"{moved.Refusal}: {moved.Detail}");
    }
}
