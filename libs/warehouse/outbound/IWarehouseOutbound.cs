using MintPlayer.Spark.Fulfilment.Contracts;
using MintPlayer.Spark.Warehouse.Outbound.Models;

namespace MintPlayer.Spark.Warehouse.Outbound;

/// <summary>What happened to an incoming <c>fulfilment-orders</c> replica (PRD §4.3 Intake).</summary>
public enum IntakeOutcome
{
    Accepted,
    Rejected,
    /// <summary>Known already: the shop's intent (holds, priority, ship-not-before) and a cancellation request were applied.</summary>
    Updated,
    /// <summary>A replica without an id, or a closed order: nothing to do.</summary>
    Ignored,
}

/// <summary>Turns the shops' <c>fulfilment-orders</c> into <see cref="OutboundOrder"/>s and answers them (PRD §4.3 Intake).</summary>
public interface IOutboundIntake
{
    /// <param name="sourceModule">The shop module the replica came from (its owner link maps the owner, Q-W23).</param>
    Task<IntakeOutcome> ReceiveAsync(string sourceModule, FulfilmentOrder order, CancellationToken cancellationToken = default);
}

/// <summary>Why an order was not released.</summary>
public enum ReleaseBlock
{
    /// <summary>The shop holds it (payment review, fraud check…).</summary>
    ShopHold,
    /// <summary>A supervisor holds it.</summary>
    ManualHold,
    /// <summary>Before <c>ShipNotBefore</c> in the site's local date.</summary>
    NotBefore,
    /// <summary><c>ShipComplete</c> and not every line can be allocated.</summary>
    ShipComplete,
    CancellationRequested,
    /// <summary>Not in a releasable state.</summary>
    NotReleasable,
    /// <summary>The site has no drop location for the plan's sortation (a Staging, PackStation or Consolidation location).</summary>
    NoDropLocation,
}

/// <summary>The outcome of a release.</summary>
/// <param name="Released">Orders that got pick work.</param>
/// <param name="Waiting">Orders that did not, and why.</param>
/// <param name="GroupIds">The pick batches (<c>WarehouseTaskGroup</c>s) created.</param>
public sealed record ReleaseReport(IReadOnlyList<string> Released, IReadOnlyDictionary<string, ReleaseBlock> Waiting, IReadOnlyList<string> GroupIds);

/// <summary>
/// Release and allocation (PRD §4.7): the shop's holds respected, <c>ShipComplete</c> honoured, stock claimed per
/// <c>IAllocationRule</c> under optimistic concurrency in the same session as the order's allocation rows, unallocatable
/// quantity backordered or cancelled per the shop's policy, and the work planned by an <c>IPickStrategy</c> and routed by
/// an <c>IPickRouter</c>.
/// </summary>
public interface IOutboundRelease
{
    /// <param name="waveId">The wave releasing them, or null (immediate or manual).</param>
    /// <param name="strategy">A pick strategy name, or null for <c>Spark:Warehouse:Picking:Strategy</c>.</param>
    Task<ReleaseReport> ReleaseAsync(IReadOnlyList<string> orderIds, string? waveId = null, string? strategy = null, string? userId = null, CancellationToken cancellationToken = default);

    /// <summary>A supervisor's hold, or its release.</summary>
    Task<bool> HoldAsync(string orderId, bool hold, string? reason, CancellationToken cancellationToken = default);
}

/// <summary>Waves (PRD §4.7 Release): cut-offs per site on the site's local wall clock (Q-W1).</summary>
public interface IOutboundWaves
{
    /// <summary>Releases every site's due cut-off (each once, even with several nodes). Answers the waves run.</summary>
    Task<IReadOnlyList<string>> RunDueAsync(CancellationToken cancellationToken = default);

    /// <summary>Releases the accepted orders of a site now, as one wave.</summary>
    Task<ReleaseReport> ReleaseSiteAsync(string siteId, string waveId, CancellationToken cancellationToken = default);
}

public enum BatchClaimOutcome
{
    Claimed,
    Renewed,
    HeldByOther,
    NotOpen,
    Unknown,
}

/// <summary>One scan-to-confirm (PRD §4.7): location (or check digits) → item → lot → quantity → tote.</summary>
/// <param name="OpId">The idempotency key of this confirmation; a re-sent confirmation is applied once.</param>
/// <param name="ScannedLocation">The location barcode, its code, or its check digits.</param>
/// <param name="ItemBarcode">The scanned item barcode (resolved with the task's item pre-selected), or null with <paramref name="ItemId"/>.</param>
/// <param name="ItemId">The item, when the handheld resolved the barcode itself (a choice the user made).</param>
/// <param name="Quantity">Base units; null: one scan's pack quantity.</param>
/// <param name="ToteId">The tote the units went into (cluster, batch and zone picking).</param>
/// <param name="OrderId">Cluster picking: the order whose tote the units went into.</param>
public sealed record PickConfirmation(
    string TaskId,
    string UserId,
    string OpId,
    string? ScannedLocation = null,
    string? ItemBarcode = null,
    string? ItemId = null,
    string? LotCode = null,
    decimal? Quantity = null,
    string? ToteId = null,
    string? OrderId = null,
    string? DeviceId = null,
    DateTimeOffset? DeviceTime = null);

public enum PickOutcome
{
    Confirmed,
    /// <summary>The op id was applied before; nothing changed.</summary>
    Replayed,
    NotHolder,
    NotOpen,
    Unknown,
    WrongLocation,
    WrongItem,
    /// <summary>Another lot than the allocated one (Q-W6: refused unless substitution is on and allowed).</summary>
    WrongLot,
    TooMany,
    ToteRequired,
    /// <summary>The tote holds another batch's or order's units.</summary>
    ToteInUse,
    /// <summary>The stock engine refused (<see cref="PickResult.Detail"/>).</summary>
    Refused,
}

/// <param name="Remaining">Base units the task still expects.</param>
public sealed record PickResult(PickOutcome Outcome, decimal Remaining = 0, string? Detail = null)
{
    public bool Succeeded => Outcome is PickOutcome.Confirmed or PickOutcome.Replayed;
}

/// <summary>Pick work (PRD §4.7): batch leases with supervisor takeover, scan-to-confirm, short picks, put-backs.</summary>
public interface IOutboundPicking
{
    /// <summary>Takes a pick batch (and its tasks) under a lease; the holder's own claim renews it.</summary>
    Task<BatchClaimOutcome> ClaimBatchAsync(string groupId, string userId, CancellationToken cancellationToken = default);

    /// <summary>A supervisor gives a batch to <paramref name="userId"/>: when its lease lapsed, or always with <paramref name="force"/>.</summary>
    Task<BatchClaimOutcome> TakeOverBatchAsync(string groupId, string userId, bool force, CancellationToken cancellationToken = default);

    Task<PickResult> ConfirmAsync(PickConfirmation confirmation, CancellationToken cancellationToken = default);

    /// <summary>
    /// The picker found less than the task expects (after confirming what was found): the rest is released and the
    /// location's remaining balance zeroed with a reason; the orders are re-allocated, backordered, cancelled or held per
    /// <c>Spark:Warehouse:ShortPick</c>; optionally a count and a replenishment are triggered.
    /// </summary>
    Task<PickResult> ShortPickAsync(string taskId, string userId, string opId, string? reasonCode = null, CancellationToken cancellationToken = default);

    /// <summary>Confirms a put-back task (a cancellation after picking): the units go back where they were picked.</summary>
    Task<PickResult> ConfirmPutBackAsync(string taskId, string userId, string opId, string? scannedLocation = null, CancellationToken cancellationToken = default);
}

/// <summary>The cancellation handshake (WD2, PRD §4.3): the warehouse answers the shop's request in its status.</summary>
public interface IOutboundCancellation
{
    /// <returns>The answer, or null when there is nothing to answer (no request, or answered already).</returns>
    Task<FulfilmentCancellationAnswer?> HandleAsync(string orderId, CancellationToken cancellationToken = default);
}

/// <summary>The packing package's part of a cancellation: a packed order can be cancelled only if its label can be voided.</summary>
public interface IPackedOrderCancellation
{
    /// <summary>
    /// Voids the order's open shipment's labels. True: nothing labelled, or voided (the order may be put back); false: the
    /// carrier refuses the void (the cancellation is refused, the shop turns it into a return).
    /// </summary>
    Task<bool> TryVoidAsync(OutboundOrder order, CancellationToken cancellationToken = default);
}

/// <summary>Where a put or a tote went at the put wall or the consolidation station.</summary>
public sealed record SlotPlacement(string OrderId, string SlotId, string SlotCode, decimal Quantity, bool OrderComplete);

/// <summary>Put wall and consolidation station (PRD §4.7 Consolidation; the W4 bookkeeping).</summary>
public interface IOutboundConsolidation
{
    /// <summary>Put wall: one article from a batch tote; the screen shows the slot of an order that still expects it.</summary>
    Task<SlotPlacement?> PutAsync(string siteId, string toteId, string itemId, decimal quantity, CancellationToken cancellationToken = default);

    /// <summary>Consolidation station: a zone tote arrives; its units go into their orders' slots.</summary>
    Task<IReadOnlyList<SlotPlacement>> ArriveToteAsync(string siteId, string toteId, CancellationToken cancellationToken = default);
}

/// <summary>Backordered lines are re-allocated when stock arrives (PRD §4.7).</summary>
public interface IOutboundBackorders
{
    /// <summary>Re-tries the backordered lines of a site (or every site) and of an item (or every item). Answers the orders that got work.</summary>
    Task<int> RetryAsync(string? siteId = null, string? itemId = null, CancellationToken cancellationToken = default);
}

/// <summary>What packing may take of an order (PRD §4.7: "released to packing only when complete, unless partial shipment is allowed").</summary>
/// <param name="Units">Per line id, the units ready at the pack station.</param>
public sealed record PackReadiness(bool Ready, IReadOnlyDictionary<string, decimal> Units, string? Reason = null);
