namespace MintPlayer.Spark.Commerce.Inventory;

/// <summary>
/// The inventory engine (commerce PRD §4.3), registered by <c>AddCommerceInventory()</c>. Every claim operation is one
/// RavenDB cluster-wide transaction over the quota shards, the chain, the claim document and the compare-exchange
/// claims of seats and buyer limits: it succeeds or fails as a whole (I2) and never oversells (I1). Every operation is
/// idempotent by claim id (or return id) and retried on a concurrency conflict (I4). Expiry is lazy: a hold stops
/// counting at <c>ExpiresAt + grace</c> whether or not the sweeper ran (I3, I5).
/// </summary>
/// <remarks>
/// After each commit the availability of every quota it changed is published to the live topic
/// <c>commerce-inventory:{quotaId}</c> (X2), and every <see cref="IInventoryCommitted"/> is told which quotas and
/// units the commit touched. Checkout calls this service; a host rarely does, except to define
/// quotas, item mappings and chains.
/// </remarks>
public interface ISparkInventory
{
    // ---- definitions ---------------------------------------------------------------------------------------

    /// <summary>Creates a quota of <paramref name="capacity"/> units over <paramref name="shardCount"/> shards (the remainder goes to the first ones). Idempotent: an existing quota is left alone.</summary>
    Task CreateQuotaAsync(string quotaId, int capacity, int shardCount, CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes a quota's capacity. Refused (<see cref="ClaimStatus.InvalidState"/>) below what is sold plus held now;
    /// each shard keeps at least its own sold and held units, the rest is spread evenly.
    /// </summary>
    Task<ClaimResult> SetCapacityAsync(string quotaId, int capacity, CancellationToken cancellationToken = default);

    /// <summary>Maps a sellable item to the quotas it counts against, with a multiplier for bundles (D15). Replaces the previous mapping.</summary>
    Task MapItemAsync(string itemId, IReadOnlyList<ItemQuota> quotas, CancellationToken cancellationToken = default);

    /// <summary>
    /// Defines a fill chain (D25): rows in fill order, each row's units in fill order. Refused
    /// (<see cref="ClaimStatus.InvalidState"/>) once a seat of the chain was given out.
    /// </summary>
    Task<ClaimResult> DefineChainAsync(string chainId, IReadOnlyList<IReadOnlyList<string>> rows, CancellationToken cancellationToken = default);

    // ---- claims --------------------------------------------------------------------------------------------

    /// <summary>Holds everything in <paramref name="request"/> until its <c>ExpiresAt</c>, or nothing.</summary>
    Task<ClaimResult> TryClaimAsync(ClaimRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes a held claim to <paramref name="target"/> (HoldFromSelection quantity change, D1): items, quotas, limits,
    /// explicit and picked seats are set to the target; a chain line that shrinks gives back its last seats, one that
    /// grows seats the extra persons as their own group. All or nothing: a refused adjustment leaves the claim as it
    /// was. On a <b>lapsed</b> held claim it re-claims (a late payment, D3/D16): whatever is still free is taken again.
    /// </summary>
    Task<ClaimResult> AdjustAsync(ClaimRequest target, CancellationToken cancellationToken = default);

    /// <summary>Moves a held claim's expiry (PaymentPending, D3). Fails with <see cref="ClaimStatus.Expired"/> once it lapsed.</summary>
    Task<ClaimResult> ExtendAsync(string claimId, DateTimeOffset expiresAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes a held claim final: its units are sold. A payer converting their own hold gets the grace (I5).
    /// <paramref name="lateLimits"/> are limit keys known only now (the card fingerprint, D29): counted at once, and the
    /// conversion is refused (<see cref="ClaimStatus.LimitExceeded"/>) when one would be exceeded.
    /// </summary>
    Task<ClaimResult> ConvertAsync(string claimId, IReadOnlyList<LimitLine>? lateLimits = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gives a whole claim back, exactly once. A held claim drops its holds; a converted one returns everything not
    /// returned yet: quota units (unless <paramref name="returnStock"/> is false: shipped goods come back through
    /// <see cref="RestockAsync"/>), seats and per-buyer counts (I4).
    /// </summary>
    Task<ClaimResult> ReleaseAsync(string claimId, ReturnReason reason, bool returnStock = true, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns part (or all) of a converted claim, exactly once per <see cref="InventoryReturn.ReturnId"/>: a refund
    /// line, a withdrawal (GA1), a refused line of a mixed basket (D40), a lost dispute keyed by the dispute id (GB1).
    /// </summary>
    Task<ClaimResult> ReturnAsync(string claimId, InventoryReturn request, CancellationToken cancellationToken = default);

    /// <summary>Puts the quota units of a return made with <c>ReturnStock = false</c> back on sale, exactly once (shipped goods received back, I4).</summary>
    Task<ClaimResult> RestockAsync(string claimId, string returnId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Exchanges part of a converted claim for something else in one cluster-wide transaction (seat change, upgrade):
    /// returns <see cref="InventoryExchange.Return"/>, claims <see cref="InventoryExchange.Target"/> and converts it. All or
    /// nothing (I2): a refused target (sold out, seat taken, limit) leaves the original claim exactly as it was. The
    /// result's <c>Units</c> are the target claim's seats. Idempotent: repeating a done exchange answers success.
    /// </summary>
    Task<ClaimResult> ExchangeAsync(InventoryExchange request, CancellationToken cancellationToken = default);

    // ---- reads ---------------------------------------------------------------------------------------------

    Task<QuotaAvailability> GetAvailabilityAsync(string quotaId, CancellationToken cancellationToken = default);

    /// <summary>Whether each unit is free, held or sold (for a seat map). Owners are never exposed.</summary>
    Task<IReadOnlyDictionary<string, UnitState>> GetUnitStatesAsync(IEnumerable<string> unitIds, CancellationToken cancellationToken = default);

    Task<InventoryClaim?> GetClaimAsync(string claimId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The quotas a sellable item counts against, with their multipliers (D15), as <see cref="MapItemAsync"/> stored
    /// them; null when the item is not mapped. The public read of the mapping: callers never load the engine's
    /// documents themselves (S18).
    /// </summary>
    Task<IReadOnlyList<ItemQuota>?> GetItemMappingAsync(string itemId, CancellationToken cancellationToken = default);
}
