namespace MintPlayer.Spark.Warehouse;

/// <summary>One allocated order line, ready to be picked from one balance (PRD §4.5 <c>OutboundOrder</c>, §4.7).</summary>
/// <param name="OrderId">The <c>OutboundOrder</c> id.</param>
/// <param name="BalanceId">The balance the allocation claimed.</param>
/// <param name="Quantity">The allocated quantity, in base units.</param>
public sealed record PickLine(
    string OrderId,
    int LineNumber,
    string ItemId,
    string? LotCode,
    string BalanceId,
    WarehouseLocationRef Location,
    decimal Quantity);

/// <summary>The released lines a strategy turns into trips: one wave or one immediate release (PRD §4.7, Release).</summary>
/// <param name="WaveId">The wave, or null for an immediate release.</param>
public sealed record PickPlanRequest(string SiteId, string? WaveId, IReadOnlyList<PickLine> Lines);

/// <summary>How the picked units are separated per order (PRD §4.7, Pick work and Consolidation).</summary>
public enum PickSortation
{
    /// <summary>Discrete: the trip holds one order, nothing to sort.</summary>
    None,

    /// <summary>Batch: picked by article, sorted per order at a put wall.</summary>
    PutWall,

    /// <summary>Cluster: one tote per order on the cart, sorted while picking.</summary>
    ToteOnCart,

    /// <summary>Zone: the order's zone totes are merged at a consolidation station.</summary>
    Consolidation,
}

/// <summary>
/// One unit of pick work, held by one picker under a lease (commerce X12). Its lines become pick tasks, one per location
/// visit, in the order <see cref="IPickRouter"/> gives.
/// </summary>
/// <param name="ZoneId">The zone the trip stays in, for zone picking; otherwise null.</param>
public sealed record PickTrip(string? ZoneId, PickSortation Sortation, IReadOnlyList<string> OrderIds, IReadOnlyList<PickLine> Lines);

/// <summary>The trips a strategy made of a release. Every line of the request appears in exactly one trip.</summary>
public sealed record PickPlan(IReadOnlyList<PickTrip> Trips);

/// <summary>
/// Turns released lines into trips (PRD §4.7 Pick work, WD5). The built-ins are
/// <see cref="WarehouseStrategyNames.Discrete"/>, <see cref="WarehouseStrategyNames.Batch"/>,
/// <see cref="WarehouseStrategyNames.Cluster"/> and <see cref="WarehouseStrategyNames.Zone"/>. A wave can feed any of them.
/// </summary>
/// <remarks>
/// A strategy only groups. It does not order the visits (<see cref="IPickRouter"/> does) and writes nothing; the engine
/// creates the <c>WarehouseTaskGroup</c> and its tasks from the plan.
/// </remarks>
public interface IPickStrategy
{
    /// <summary>The name a wave or a site's configuration selects this strategy by.</summary>
    string Name { get; }

    ValueTask<PickPlan> PlanAsync(PickPlanRequest request, CancellationToken cancellationToken = default);
}
