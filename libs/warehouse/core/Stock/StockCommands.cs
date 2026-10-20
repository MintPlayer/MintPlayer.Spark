using MintPlayer.Spark.Warehouse.Models;

namespace MintPlayer.Spark.Warehouse.Stock;

/// <summary>
/// Who and what a stock command is (PRD §4.5): the op id is the idempotency key and becomes the movement id (WI4), so a
/// command retried after a lost answer, or replayed from an offline queue, is applied at most once.
/// </summary>
/// <param name="OpId">Unique per command, chosen by the caller (a handheld's UUID, a task line's key).</param>
/// <param name="UserId">The person, stamped explicitly (raw writes are not audit-stamped).</param>
/// <param name="DeviceTime">The device's clock: recorded, never used for ordering (§4.14).</param>
/// <param name="Reference">What the command serves: a task, an order line, a shipment line, a receipt.</param>
public sealed record StockOp(string OpId, string? UserId = null, string? DeviceId = null, DateTimeOffset? DeviceTime = null, string? Reference = null);

/// <summary>Stock arrives at a location (receipt, blind receipt, restocked return). The first put-away creates the balance.</summary>
public sealed record ReceiveStock(StockOp Op, string LocationId, string ItemId, decimal Quantity, string? LotCode = null, DateOnly? Expiry = null);

/// <summary>Stock moves from a balance to a location (put-away, replenishment, move, put-back).</summary>
/// <param name="CarryAllocated">Base units of the source's allocation that travel with it (a pick carries its allocation to the tote).</param>
public sealed record MoveStock(StockOp Op, string FromBalanceId, string ToLocationId, decimal Quantity, decimal CarryAllocated = 0);

/// <summary>A correction with a reason code (count variance, damage, found stock). <paramref name="Delta"/> is signed.</summary>
public sealed record AdjustStock(StockOp Op, string LocationId, string ItemId, string? LotCode, decimal Delta, string ReasonCode, DateOnly? Expiry = null);

/// <summary>Claims (<paramref name="Quantity"/> &gt; 0) or releases (&lt; 0) allocation on a balance.</summary>
public sealed record AllocateStock(StockOp Op, string BalanceId, decimal Quantity);

/// <summary>Ship-confirm of allocated stock: on hand and allocation down, the channel's shipped total up, in one transaction (WI6).</summary>
public sealed record ShipStock(StockOp Op, string BalanceId, decimal Quantity, string Channel);

/// <summary>A channel's open (accepted, unshipped) quantity changes: an order accepted (+) or cancelled (−) (§4.4).</summary>
public sealed record ChangeOpenQuantity(StockOp Op, string OrganizationId, string ItemId, string Channel, decimal Delta);

/// <summary>A lot's status changes (quarantine, release, recall); its balances' sellability and the stock level follow.</summary>
public sealed record ChangeLotStatus(StockOp Op, string LotId, StockLotStatus Status, string? Reason = null);

public enum StockOutcome
{
    /// <summary>Applied now.</summary>
    Applied,
    /// <summary>The op id was applied before; nothing changed (WI4).</summary>
    Replayed,
    /// <summary>Refused by a rule; nothing changed. <see cref="StockResult.Refusal"/> says why.</summary>
    Refused,
}

/// <summary>Why a stock command was refused.</summary>
public enum StockRefusal
{
    UnknownLocation,
    InactiveLocation,
    UnknownItem,
    UnknownBalance,
    /// <summary>Would make on hand negative (WI1).</summary>
    InsufficientStock,
    /// <summary>Would allocate more than on hand, or release more than allocated (WI2).</summary>
    InsufficientAvailable,
    /// <summary>The lot is recalled or quarantined: no allocation, pick or ship (round 5, GA7).</summary>
    LotBlocked,
    /// <summary>An adjustment without a known, active adjustment reason code.</summary>
    ReasonRequired,
    /// <summary>A lot-tracked item without a lot code, or a lot code on an untracked item.</summary>
    LotMismatch,
    InvalidQuantity,
    /// <summary>The op id was used before for a different command.</summary>
    OpIdReused,
}

/// <summary>The answer to a stock command.</summary>
/// <param name="Attempts">Sessions it took (1 = no concurrency retry); the W1 guard records the retry rate from it.</param>
public sealed record StockResult(StockOutcome Outcome, string MovementId, StockRefusal? Refusal = null, string? Detail = null, int Attempts = 1)
{
    public bool Succeeded => Outcome is StockOutcome.Applied or StockOutcome.Replayed;
}

/// <summary>
/// The stock engine (PRD §4.5 "Concurrency"): every command writes, in one session with optimistic concurrency (or one
/// cluster-wide transaction), its source and target balances, the movement, and the stock level, and retries the whole
/// session on a concurrency conflict. Indexes never decide: callers find candidates by query, the engine loads and
/// checks each balance it changes.
/// </summary>
public interface IWarehouseStock
{
    Task<StockResult> ReceiveAsync(ReceiveStock command, CancellationToken cancellationToken = default);
    Task<StockResult> MoveAsync(MoveStock command, CancellationToken cancellationToken = default);
    /// <summary>A pick: <see cref="MoveAsync"/> carrying the picked quantity's allocation, recorded as <see cref="StockMovementKind.Pick"/>.</summary>
    Task<StockResult> PickAsync(MoveStock command, CancellationToken cancellationToken = default);
    Task<StockResult> AdjustAsync(AdjustStock command, CancellationToken cancellationToken = default);
    Task<StockResult> AllocateAsync(AllocateStock command, CancellationToken cancellationToken = default);
    Task<StockResult> ShipAsync(ShipStock command, CancellationToken cancellationToken = default);
    Task<StockResult> ChangeOpenAsync(ChangeOpenQuantity command, CancellationToken cancellationToken = default);
    Task<StockResult> ChangeLotStatusAsync(ChangeLotStatus command, CancellationToken cancellationToken = default);
}

/// <summary>Raised when a command still conflicts after every retry (a pathological hot spot); nothing was applied.</summary>
public sealed class StockConcurrencyException(string opId, int attempts)
    : Exception($"Stock command '{opId}' conflicted {attempts} times; nothing was applied. Retry it with the same op id.")
{
    public string OpId { get; } = opId;
    public int Attempts { get; } = attempts;
}
