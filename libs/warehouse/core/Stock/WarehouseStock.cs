using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Warehouse.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;

namespace MintPlayer.Spark.Warehouse.Stock;

/// <summary>
/// <see cref="IWarehouseStock"/> (PRD §4.5 "Concurrency", WI1–WI6). Each command:
/// <list type="number">
/// <item>opens one session: optimistic concurrency on every document it writes, or a cluster-wide transaction when
/// <c>Spark:Warehouse:Stock:ClusterWide</c> is on (a multi-node cluster, the pattern of <c>UserStore</c>);</item>
/// <item>loads the movement id first: a known op id is <see cref="StockOutcome.Replayed"/> (WI4);</item>
/// <item>loads and checks the balances it changes (WI1, WI2), creates a missing target balance with a must-not-exist
/// change vector (the atomic first put-away, commerce X3: two first put-aways conflict instead of one overwriting the
/// other), changes the <see cref="StockLevel"/> and its published copies in the same session (WI6), and stores the
/// movement under the op id (WI3: the ledger is written with the balances or not at all);</item>
/// <item>retries the whole session on a concurrency conflict, up to <c>Spark:Warehouse:Stock:MaxAttempts</c>.</item>
/// </list>
/// </summary>
internal sealed partial class WarehouseStock : IWarehouseStock
{
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IOptions<SparkWarehouseOptions> options;
    [Inject] private readonly TimeProvider timeProvider;
    [Inject] private readonly IStockLevelPublisher levelPublisher;
    [Inject] private readonly IEnumerable<ILotStatusObserver> lotObservers;
    [Inject] private readonly ILogger<WarehouseStock> logger;

    /// <summary>Tests only: runs after a command's changes are staged and before its commit (to force an interleaving).</summary>
    internal Func<StockOp, int, Task>? BeforeSaveForTests { get; set; }

    public Task<StockResult> ReceiveAsync(ReceiveStock command, CancellationToken cancellationToken = default)
        => RunAsync(command.Op, StockMovementKind.Receive, async (s, movement) =>
        {
            if (command.Quantity <= 0)
                return Refuse(StockRefusal.InvalidQuantity, "A receipt needs a positive quantity.");
            var location = await s.LocationAsync(command.LocationId);
            if (location is null)
                return Refuse(StockRefusal.UnknownLocation, command.LocationId);
            if (!location.Active)
                return Refuse(StockRefusal.InactiveLocation, location.Code);
            var item = await s.ItemAsync(command.ItemId);
            if (item is null)
                return Refuse(StockRefusal.UnknownItem, command.ItemId);
            if (LotProblem(item, command.LotCode) is { } lotProblem)
                return lotProblem;

            var lot = await s.LotForReceiptAsync(item, command.LotCode, command.Expiry, command.Op.Reference);
            var balance = await s.BalanceForAsync(location, item, lot);
            balance.OnHand += command.Quantity;
            balance.ReceivedAt ??= s.Now;
            balance.UpdatedAt = s.Now;
            if (balance.Sellable)
            {
                (await s.LevelAsync(item)).OnHandSellable += command.Quantity;
                s.ObserveLot(lot);
            }
            s.TouchHistory(location);

            Fill(movement, item, lot?.LotCode, null, balance, null, location.Id, command.Quantity);
            return null;
        }, cancellationToken);

    public Task<StockResult> MoveAsync(MoveStock command, CancellationToken cancellationToken = default)
        => MoveCoreAsync(command, StockMovementKind.Move, cancellationToken);

    public Task<StockResult> PickAsync(MoveStock command, CancellationToken cancellationToken = default)
        => MoveCoreAsync(command, StockMovementKind.Pick, cancellationToken);

    private Task<StockResult> MoveCoreAsync(MoveStock command, StockMovementKind kind, CancellationToken cancellationToken)
        => RunAsync(command.Op, kind, async (s, movement) =>
        {
            if (command.Quantity <= 0 || command.CarryAllocated < 0 || command.CarryAllocated > command.Quantity)
                return Refuse(StockRefusal.InvalidQuantity, "A move needs a positive quantity, and can carry at most that much allocation.");
            var from = await s.BalanceAsync(command.FromBalanceId);
            if (from is null)
                return Refuse(StockRefusal.UnknownBalance, command.FromBalanceId);
            var item = await s.ItemAsync(from.ItemId);
            if (item is null)
                return Refuse(StockRefusal.UnknownItem, from.ItemId);
            var lot = from.LotCode is null ? null : await s.LotAsync(StockLot.IdFor(from.ItemId, from.LotCode));
            if (kind == StockMovementKind.Pick && lot is { Status: not StockLotStatus.Released })
                return Refuse(StockRefusal.LotBlocked, $"Lot {lot.LotCode} is {lot.Status}.");
            if (from.OnHand - command.Quantity < 0)
                return Refuse(StockRefusal.InsufficientStock, $"{from.OnHand} on hand.");
            if (from.Allocated - command.CarryAllocated < 0 || from.OnHand - command.Quantity < from.Allocated - command.CarryAllocated)
                return Refuse(StockRefusal.InsufficientAvailable, $"{from.Allocated} of {from.OnHand} allocated.");

            var target = await s.LocationAsync(command.ToLocationId);
            if (target is null || target.SiteId != from.SiteId)
                return Refuse(StockRefusal.UnknownLocation, command.ToLocationId);
            if (!target.Active)
                return Refuse(StockRefusal.InactiveLocation, target.Code);
            var to = await s.BalanceForAsync(target, item, lot);
            if (to.Id == from.Id)
                return Refuse(StockRefusal.InvalidQuantity, "Source and target are the same balance.");

            from.OnHand -= command.Quantity;
            from.Allocated -= command.CarryAllocated;
            from.UpdatedAt = s.Now;
            to.OnHand += command.Quantity;
            to.Allocated += command.CarryAllocated;
            to.ReceivedAt ??= from.ReceivedAt ?? s.Now;
            to.UpdatedAt = s.Now;
            var sellableDelta = (to.Sellable ? command.Quantity : 0) - (from.Sellable ? command.Quantity : 0);
            if (sellableDelta != 0)
                (await s.LevelAsync(item)).OnHandSellable += sellableDelta;
            if (to.Sellable)
                s.ObserveLot(lot);
            s.TouchHistory(target);

            Fill(movement, item, from.LotCode, from, to, from.LocationId, target.Id, command.Quantity);
            return null;
        }, cancellationToken);

    public Task<StockResult> AdjustAsync(AdjustStock command, CancellationToken cancellationToken = default)
        => RunAsync(command.Op, StockMovementKind.Adjust, async (s, movement) =>
        {
            if (command.Delta == 0)
                return Refuse(StockRefusal.InvalidQuantity, "An adjustment needs a non-zero delta.");
            ReasonCode? reason = null;
            try
            {
                reason = await s.Session.LoadAsync<ReasonCode>(ReasonCode.IdFor(command.ReasonCode), s.Token);
            }
            catch (ArgumentException)
            {
            }
            if (reason is not { Active: true })
                return Refuse(StockRefusal.ReasonRequired, command.ReasonCode);

            var location = await s.LocationAsync(command.LocationId);
            if (location is null)
                return Refuse(StockRefusal.UnknownLocation, command.LocationId);
            var item = await s.ItemAsync(command.ItemId);
            if (item is null)
                return Refuse(StockRefusal.UnknownItem, command.ItemId);
            if (LotProblem(item, command.LotCode) is { } lotProblem)
                return lotProblem;

            StockBalance? balance;
            StockLot? lot;
            if (command.Delta > 0)
            {
                if (!location.Active)
                    return Refuse(StockRefusal.InactiveLocation, location.Code);
                lot = await s.LotForReceiptAsync(item, command.LotCode, command.Expiry, command.Op.Reference);
                balance = await s.BalanceForAsync(location, item, lot);
                balance.ReceivedAt ??= s.Now;
            }
            else
            {
                balance = await s.BalanceAsync(StockBalance.IdFor(location.Id!, item.Id!, command.LotCode));
                if (balance is null || balance.OnHand + command.Delta < 0)
                    return Refuse(StockRefusal.InsufficientStock, $"{balance?.OnHand ?? 0} on hand.");
                if (balance.OnHand + command.Delta < balance.Allocated)
                    return Refuse(StockRefusal.InsufficientAvailable, $"{balance.Allocated} of {balance.OnHand} allocated; release the allocation first (short pick).");
            }

            balance.OnHand += command.Delta;
            balance.UpdatedAt = s.Now;
            if (balance.Sellable)
                (await s.LevelAsync(item)).OnHandSellable += command.Delta;
            if (command.Delta > 0 && balance.Sellable && balance.LotCode is not null)
                s.ObserveLot(await s.LotAsync(StockLot.IdFor(balance.ItemId, balance.LotCode)));
            s.TouchHistory(location);

            movement.ReasonCode = reason.Code;
            Fill(movement, item, balance.LotCode, command.Delta < 0 ? balance : null, command.Delta > 0 ? balance : null, location.Id, location.Id, command.Delta);
            return null;
        }, cancellationToken);

    public Task<StockResult> AllocateAsync(AllocateStock command, CancellationToken cancellationToken = default)
        => RunAsync(command.Op, StockMovementKind.Allocate, async (s, movement) =>
        {
            if (command.Quantity == 0)
                return Refuse(StockRefusal.InvalidQuantity, "An allocation needs a non-zero quantity.");
            var balance = await s.BalanceAsync(command.BalanceId);
            if (balance is null)
                return Refuse(StockRefusal.UnknownBalance, command.BalanceId);
            if (command.Quantity > 0)
            {
                var lot = balance.LotCode is null ? null : await s.LotAsync(StockLot.IdFor(balance.ItemId, balance.LotCode));
                if (lot is { Status: not StockLotStatus.Released })
                    return Refuse(StockRefusal.LotBlocked, $"Lot {lot.LotCode} is {lot.Status}.");
                if (!balance.Sellable || balance.Allocated + command.Quantity > balance.OnHand)
                    return Refuse(StockRefusal.InsufficientAvailable, $"{balance.OnHand - balance.Allocated} available.");
                s.ObserveLot(lot);
            }
            else if (balance.Allocated + command.Quantity < 0)
                return Refuse(StockRefusal.InsufficientAvailable, $"{balance.Allocated} allocated.");

            balance.Allocated += command.Quantity;
            balance.UpdatedAt = s.Now;
            movement.OrganizationId = balance.OrganizationId;
            movement.ItemId = balance.ItemId;
            movement.LotCode = balance.LotCode;
            movement.FromBalanceId = balance.Id;
            movement.FromLocationId = balance.LocationId;
            movement.Quantity = command.Quantity;
            return null;
        }, cancellationToken);

    public Task<StockResult> ShipAsync(ShipStock command, CancellationToken cancellationToken = default)
        => RunAsync(command.Op, StockMovementKind.Ship, async (s, movement) =>
        {
            if (command.Quantity <= 0 || string.IsNullOrWhiteSpace(command.Channel))
                return Refuse(StockRefusal.InvalidQuantity, "A shipment needs a positive quantity and a channel.");
            var balance = await s.BalanceAsync(command.BalanceId);
            if (balance is null)
                return Refuse(StockRefusal.UnknownBalance, command.BalanceId);
            var item = await s.ItemAsync(balance.ItemId);
            if (item is null)
                return Refuse(StockRefusal.UnknownItem, balance.ItemId);
            var lot = balance.LotCode is null ? null : await s.LotAsync(StockLot.IdFor(balance.ItemId, balance.LotCode));
            if (lot is { Status: StockLotStatus.Recalled })
                return Refuse(StockRefusal.LotBlocked, $"Lot {lot.LotCode} is recalled.");
            if (command.Quantity > balance.Allocated)
                return Refuse(StockRefusal.InsufficientAvailable, $"Only allocated stock ships: {balance.Allocated} allocated.");

            balance.OnHand -= command.Quantity;
            balance.Allocated -= command.Quantity;
            balance.UpdatedAt = s.Now;

            // WI6: on hand and the channel's shipped total change in this one document, in this one transaction.
            var level = await s.LevelAsync(item);
            if (balance.Sellable)
                level.OnHandSellable -= command.Quantity;
            var channel = level.Channel(command.Channel);
            channel.ShippedTotal += command.Quantity;
            channel.Open = Math.Max(0, channel.Open - command.Quantity);
            s.LevelChanged(level);

            movement.Channel = command.Channel;
            Fill(movement, item, balance.LotCode, balance, null, balance.LocationId, null, command.Quantity);
            return null;
        }, cancellationToken);

    public Task<StockResult> ChangeOpenAsync(ChangeOpenQuantity command, CancellationToken cancellationToken = default)
        => RunAsync(command.Op, StockMovementKind.Open, async (s, movement) =>
        {
            if (command.Delta == 0 || string.IsNullOrWhiteSpace(command.Channel))
                return Refuse(StockRefusal.InvalidQuantity, "An open-quantity change needs a delta and a channel.");
            var item = await s.ItemAsync(command.ItemId);
            if (item is null || item.OrganizationId != command.OrganizationId)
                return Refuse(StockRefusal.UnknownItem, command.ItemId);
            var level = await s.LevelAsync(item);
            var channel = level.Channel(command.Channel);
            if (channel.Open + command.Delta < 0)
                return Refuse(StockRefusal.InvalidQuantity, $"{channel.Open} open on channel {command.Channel}.");
            channel.Open += command.Delta;
            s.LevelChanged(level);

            movement.OrganizationId = item.OrganizationId;
            movement.ItemId = item.Id!;
            movement.Channel = command.Channel;
            movement.Quantity = command.Delta;
            return null;
        }, cancellationToken);

    public async Task<StockResult> ChangeLotStatusAsync(ChangeLotStatus command, CancellationToken cancellationToken = default)
    {
        StockLot? changed = null;
        var result = await RunAsync(command.Op, StockMovementKind.Status, async (s, movement) =>
        {
            var lot = await s.LotAsync(command.LotId);
            if (lot is null)
                return Refuse(StockRefusal.UnknownBalance, command.LotId);
            var item = await s.ItemAsync(lot.ItemId);
            if (item is null)
                return Refuse(StockRefusal.UnknownItem, lot.ItemId);

            lot.Status = command.Status;
            if (command.Status == StockLotStatus.Recalled)
            {
                lot.RecalledAt = s.Now;
                lot.RecallReason = command.Reason;
            }

            // The lot's balances, from the stock index (fresh: waits for it to catch up). A balance written after this
            // query reads the new status when it is created (StockSession.BalanceForAsync), and the nightly stock
            // reconcile corrects any that raced (PRD §13, WM3).
            foreach (var balance in await StockQueries.BalancesOfLotAsync(s.Session, lot.ItemId, lot.LotCode, s.Token))
            {
                s.Track(balance);
                var location = await s.LocationAsync(balance.LocationId);
                var sellable = location is not null && await s.IsSellableAsync(location, lot);
                if (sellable == balance.Sellable)
                    continue;
                (await s.LevelAsync(item)).OnHandSellable += sellable ? balance.OnHand : -balance.OnHand;
                balance.Sellable = sellable;
                balance.UpdatedAt = s.Now;
            }

            movement.OrganizationId = lot.OrganizationId;
            movement.ItemId = lot.ItemId;
            movement.LotCode = lot.LotCode;
            movement.ReasonCode = command.Status.ToString();
            changed = lot;
            return null;
        }, cancellationToken);

        if (result.Outcome == StockOutcome.Applied && changed is not null)
        {
            foreach (var observer in lotObservers)
                await observer.OnLotStatusChangedAsync(changed, cancellationToken);
        }
        return result;
    }

    private static (StockRefusal Refusal, string Detail)? Refuse(StockRefusal refusal, string detail) => (refusal, detail);

    private static (StockRefusal Refusal, string Detail)? LotProblem(WarehouseItem item, string? lotCode)
    {
        var hasLot = !string.IsNullOrWhiteSpace(lotCode);
        if (item.Tracking == ItemTracking.LotAndExpiry && !hasLot)
            return Refuse(StockRefusal.LotMismatch, $"{item.Sku} is lot-tracked: a lot code is required.");
        if (item.Tracking == ItemTracking.None && hasLot)
            return Refuse(StockRefusal.LotMismatch, $"{item.Sku} is not lot-tracked.");
        return null;
    }

    private static void Fill(StockMovement movement, WarehouseItem item, string? lotCode, StockBalance? from, StockBalance? to,
        string? fromLocationId, string? toLocationId, decimal quantity)
    {
        movement.OrganizationId = item.OrganizationId;
        movement.ItemId = item.Id!;
        movement.LotCode = lotCode;
        movement.FromBalanceId = from?.Id;
        movement.ToBalanceId = to?.Id;
        movement.FromLocationId = fromLocationId;
        movement.ToLocationId = toLocationId;
        movement.Quantity = quantity;
    }

    private async Task<StockResult> RunAsync(StockOp op, StockMovementKind kind,
        Func<StockSession, StockMovement, Task<(StockRefusal Refusal, string Detail)?>> body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(op);
        ArgumentException.ThrowIfNullOrWhiteSpace(op.OpId);
        var movementId = StockMovement.IdFor(op.OpId);
        var stock = options.Value.Stock;
        var attempts = Math.Max(1, stock.MaxAttempts);

        for (var attempt = 1; ; attempt++)
        {
            using var session = OpenSession(stock.ClusterWide);
            var existing = await session.LoadAsync<StockMovement>(movementId, cancellationToken);
            if (existing is not null)
            {
                return existing.Kind == kind
                    ? new StockResult(StockOutcome.Replayed, movementId, Attempts: attempt)
                    : new StockResult(StockOutcome.Refused, movementId, StockRefusal.OpIdReused, $"Op id '{op.OpId}' was a {existing.Kind}.", attempt);
            }

            var now = timeProvider.GetUtcNow();
            var context = new StockSession(session, stock.ClusterWide, now, cancellationToken);
            var movement = new StockMovement
            {
                Id = movementId,
                OpId = op.OpId,
                Kind = kind,
                UserId = op.UserId,
                DeviceId = op.DeviceId,
                DeviceTime = op.DeviceTime,
                Reference = op.Reference,
                ServerTime = now,
            };

            var refusal = await body(context, movement);
            if (refusal is { } refused)
                return new StockResult(StockOutcome.Refused, movementId, refused.Refusal, refused.Detail, attempt);

            await context.StoreNewAsync(movement, movementId);
            if (BeforeSaveForTests is { } hook)
                await hook(op, attempt);
            foreach (var level in context.ChangedLevels)
            {
                level.Version++;
                await levelPublisher.PublishAsync(session, level, cancellationToken);
            }

            try
            {
                await session.SaveChangesAsync(cancellationToken);
                return new StockResult(StockOutcome.Applied, movementId, Attempts: attempt);
            }
            catch (ConcurrencyException) when (attempt < attempts)
            {
                // Another command changed a balance, the level, or applied the same op id: start over with fresh reads.
                logger.LogDebug("Stock command {OpId} conflicted (attempt {Attempt}); retrying.", op.OpId, attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(1, 4 * attempt)), cancellationToken);
            }
            catch (ConcurrencyException)
            {
                throw new StockConcurrencyException(op.OpId, attempt);
            }
        }
    }

    private IAsyncDocumentSession OpenSession(bool clusterWide)
    {
        if (clusterWide)
            return documentStore.OpenAsyncSession(new Raven.Client.Documents.Session.SessionOptions { TransactionMode = TransactionMode.ClusterWide });

        var session = documentStore.OpenAsyncSession();
        // Every loaded document written back is checked against the change vector it was read with; a new document
        // must not exist yet (WI1–WI4 under concurrency).
        session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
        return session;
    }
}
