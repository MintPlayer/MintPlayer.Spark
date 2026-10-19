namespace MintPlayer.Spark.Commerce.Inventory;

/// <summary><see cref="N"/> units straight from a quota (a caller that resolved its own bundle passes the multiplied count).</summary>
public sealed record QuotaLine(string QuotaId, int N);

/// <summary>
/// <see cref="N"/> of a sellable item. The engine resolves it through the item's mapping
/// (<see cref="ISparkInventory.MapItemAsync"/>): <c>N × multiplier</c> units from every quota the item counts against
/// (D15), so a bundle of two day passes takes two units of each day's quota.
/// </summary>
public sealed record ItemLine(string ItemId, int N);

/// <summary>
/// A per-buyer limit (D16): at most <see cref="Max"/> units under <see cref="Key"/> across every order and live hold.
/// One line per linked identifier (<see cref="InventoryLimitKeys.For"/>): account, visitor, e-mail, address, card.
/// </summary>
public sealed record LimitLine(string Key, int Max, int N);

/// <summary><see cref="N"/> seats together from a chain, by chain fill (best available, D25).</summary>
public sealed record ChainLine(string ChainId, int N);

/// <summary>
/// Seats the buyer picked themselves (pick your seat, D7), with the row they sit in, in seat order. The server checks
/// the single-empty-seat rule against the row before it claims (<see cref="SingleSeatRule"/>).
/// </summary>
/// <param name="RowUnits">Every unit of the row, in seat order (aisle to aisle).</param>
/// <param name="Picked">The units picked in this row; each is one of <paramref name="RowUnits"/>.</param>
/// <param name="Rule">Overrides <see cref="InventoryEngineOptions.SingleSeatRule"/> for this row (a sale's own setting).</param>
public sealed record SeatPick(IReadOnlyList<string> RowUnits, IReadOnlyList<string> Picked, SingleSeatRule? Rule = null);

/// <summary>
/// Everything one claim takes, all or nothing (I2). <see cref="ISparkInventory.AdjustAsync"/> reads it as the claim's
/// new target shape.
/// </summary>
/// <param name="ClaimId">The claim document id; every operation is idempotent by it.</param>
/// <param name="Owner">The basket or order that holds the claim.</param>
/// <param name="ExpiresAt">When the hold lapses (lazily, plus grace: I5).</param>
/// <param name="Quotas">Units straight from quotas.</param>
/// <param name="Units">Numbered units (seats) by id.</param>
/// <param name="Limits">Per-buyer limit keys.</param>
/// <param name="Items">Sellable items, resolved through their quota mappings.</param>
/// <param name="BestAvailable">Seats by chain fill.</param>
/// <param name="Picks">Seats the buyer picked, checked against the single-seat rule.</param>
public sealed record ClaimRequest(
    string ClaimId,
    string Owner,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<QuotaLine> Quotas,
    IReadOnlyList<string>? Units = null,
    IReadOnlyList<LimitLine>? Limits = null,
    IReadOnlyList<ItemLine>? Items = null,
    IReadOnlyList<ChainLine>? BestAvailable = null,
    IReadOnlyList<SeatPick>? Picks = null);

public enum ClaimStatus
{
    /// <summary>Done: held, extended, converted, released or returned.</summary>
    Held,
    /// <summary>A quota (or a chain) has no room left.</summary>
    SoldOut,
    /// <summary>A numbered unit is held or sold by another claim.</summary>
    UnitTaken,
    /// <summary>A per-buyer limit would be exceeded.</summary>
    LimitExceeded,
    /// <summary>The claim lapsed (or a part of it was taken over after expiry plus grace).</summary>
    Expired,
    /// <summary>The claim, quota, item or chain does not exist.</summary>
    NotFound,
    /// <summary>The operation does not apply to the claim's state (or to what the request names).</summary>
    InvalidState,
    /// <summary>Concurrency conflicts outlasted <see cref="InventoryEngineOptions.MaxAttempts"/>.</summary>
    Conflict,
    /// <summary>A chain has enough free seats, but not side by side in one row.</summary>
    NotTogether,
    /// <summary>A pick would leave a single empty seat in its row (<see cref="SingleSeatRule"/>); the subject names it.</summary>
    LeavesSingleSeat,
}

/// <summary>
/// The outcome of an engine operation. <see cref="Subject"/> names the quota, unit, limit key or chain that refused it.
/// <see cref="Units"/> are the seats a claim holds afterwards (chain fill chose them).
/// </summary>
public sealed record ClaimResult(ClaimStatus Status, string? Subject = null)
{
    public bool Succeeded => Status == ClaimStatus.Held;

    /// <summary>Every unit the claim holds after a successful <c>TryClaim</c> or <c>Adjust</c>; null otherwise.</summary>
    public IReadOnlyList<string>? Units { get; init; }

    public static readonly ClaimResult Ok = new(ClaimStatus.Held);
}

/// <summary>What a quota has left for sale right now: capacity minus sold minus live holds.</summary>
public sealed record QuotaAvailability(string QuotaId, int Capacity, int Sold, int Held, int Available);

/// <summary>Whether a numbered unit is free, held (by anyone) or sold. Owners are never exposed.</summary>
public enum UnitState { Free, Held, Sold }

/// <summary>The single-empty-seat rule of pick your seat (§4.3).</summary>
public enum SingleSeatRule
{
    /// <summary>Any pick is accepted.</summary>
    Off,
    /// <summary>A pick may not leave one free seat between two taken seats.</summary>
    BetweenTaken,
    /// <summary>Also not between a taken seat and the row's end (the aisle). The default.</summary>
    BetweenTakenOrRowEnd,
}

/// <summary>
/// Why units go back (I4). Every return path names itself, so the claim's ledger shows why each counter came back.
/// </summary>
public enum ReturnReason
{
    /// <summary>A held claim lapsed (the sweeper, or a takeover).</summary>
    Expired,
    /// <summary>The payment failed or was cancelled.</summary>
    PaymentFailed,
    /// <summary>The buyer or the seller cancelled.</summary>
    Cancelled,
    /// <summary>A refund (seller policy, mass refund).</summary>
    Refund,
    /// <summary>The buyer withdrew within 14 days (GA1, CRD Art. 9–16). Shipped goods: return with <c>ReturnStock = false</c>, then restock.</summary>
    Withdrawal,
    /// <summary>A line of a mixed basket was refused or voided (D40, <c>Basket:OnLineFailure</c>).</summary>
    RefusedLine,
    /// <summary>A chargeback was lost (GB1): keyed by the dispute id. A won dispute returns nothing.</summary>
    DisputeLost,
    /// <summary>A ticket was resold at face value; the new buyer's claim takes the unit.</summary>
    Resale,
    /// <summary>A payment arrived after the hold lapsed and the units could not be re-claimed (D3, D16).</summary>
    LatePayment,
    /// <summary>Anything else the host returns.</summary>
    Other,
}

/// <summary>
/// A return from a converted claim (I4): part of it, or everything still sold. Idempotent by <see cref="ReturnId"/>:
/// a refund id, a withdrawal id, a dispute id, an order-line id.
/// </summary>
/// <param name="ReturnId">The key that makes the return happen exactly once.</param>
/// <param name="Reason">Why.</param>
/// <param name="Items">Items to return, resolved through their mappings. With <paramref name="Quotas"/> and <paramref name="Units"/> all null: everything still sold.</param>
/// <param name="Quotas">Units to return straight to quotas.</param>
/// <param name="Units">Numbered units (seats) to return.</param>
/// <param name="Limits">Per-buyer counts to return (key and count); null returns them in proportion: all of them on a full return, none on a partial one unless named.</param>
/// <param name="ReturnStock">
/// False for <b>shipped physical goods</b>: the quota units stay sold until <see cref="ISparkInventory.RestockAsync"/>
/// (the shop's return disposition or the warehouse's restock event). Seats always come back.
/// </param>
public sealed record InventoryReturn(
    string ReturnId,
    ReturnReason Reason,
    IReadOnlyList<ItemLine>? Items = null,
    IReadOnlyList<QuotaLine>? Quotas = null,
    IReadOnlyList<string>? Units = null,
    IReadOnlyList<LimitLine>? Limits = null,
    bool ReturnStock = true)
{
    internal bool IsEverything => Items is null && Quotas is null && Units is null;
}

/// <summary>An item's share of one quota (D15): every unit of the item takes <see cref="Multiplier"/> units of the quota.</summary>
public sealed record ItemQuota(string QuotaId, int Multiplier = 1);
