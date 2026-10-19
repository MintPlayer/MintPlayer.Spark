namespace MintPlayer.Spark.Warehouse;

/// <summary>
/// One stock balance that could serve an order line: a read-only snapshot of a <c>StockBalance</c> (PRD §4.5), as the
/// allocation query returned it.
/// </summary>
/// <param name="BalanceId">The balance document id, <c>{locationId}/{itemId}/{lotKey}</c>.</param>
/// <param name="LotCode">The lot, or null for items without lot tracking.</param>
/// <param name="Expiry">The lot's expiry (a copy kept on the balance for FEFO), or null.</param>
/// <param name="ReceivedAt">When the stock was first received, for FIFO and for FEFO ties, or null when unknown.</param>
/// <param name="Available"><c>OnHand − Allocated</c> in base units, at the time of the query.</param>
public sealed record AllocationCandidate(
    string BalanceId,
    WarehouseLocationRef Location,
    string? LotCode,
    DateOnly? Expiry,
    DateTimeOffset? ReceivedAt,
    decimal Available);

/// <summary>An order line that needs stock (PRD §4.7, Allocation).</summary>
/// <param name="OrderId">The <c>OutboundOrder</c> id, <c>outbound/{fulfilmentOrderId}</c>.</param>
/// <param name="Quantity">The quantity still to allocate, in base units.</param>
/// <param name="MinimumShelfLifeDays">The item's minimum shelf life. FEFO sorts by expiry minus this (Odoo's "removal date").</param>
/// <param name="Today">The site's local date (K10: from TimeProvider and the site's zone), for shelf-life checks.</param>
/// <param name="RequiredLotCode">A lot the line must come from, or null.</param>
public sealed record AllocationRequest(
    string SiteId,
    string OwnerId,
    string OrderId,
    int LineNumber,
    string ItemId,
    decimal Quantity,
    int MinimumShelfLifeDays,
    DateOnly Today,
    string? RequiredLotCode = null);

/// <summary>
/// Decides the order in which stock is claimed for an order line (PRD §4.7 Allocation, WD4; configuration
/// <c>Spark:Warehouse:Allocation:Rule</c>, §4.13).
/// </summary>
/// <remarks>
/// Allocation takes its candidates from a query, then loads each balance and claims it under optimistic concurrency
/// (§4.5, "Indexes never decide"). A rule therefore only <b>ranks</b>: it returns the candidates in the order to try,
/// leaving out any it refuses (an expired lot, a lot inside the minimum shelf life, a quarantined zone). The engine
/// claims in that order and raises <c>Allocated</c> in the same session as the order's allocation rows; a stale
/// candidate can be skipped, never oversold. The built-ins are <see cref="WarehouseStrategyNames.Fifo"/>,
/// <see cref="WarehouseStrategyNames.Fefo"/> (ties: the lowest lot, then the earliest receipt) and
/// <see cref="WarehouseStrategyNames.ClosestLocation"/>.
/// </remarks>
public interface IAllocationRule
{
    /// <summary>The name configuration selects this rule by, per owner and per item.</summary>
    string Name { get; }

    /// <summary>The candidates to claim, in order. Must not return a candidate that was not passed in.</summary>
    ValueTask<IReadOnlyList<AllocationCandidate>> RankAsync(
        AllocationRequest request,
        IReadOnlyList<AllocationCandidate> candidates,
        CancellationToken cancellationToken = default);
}
