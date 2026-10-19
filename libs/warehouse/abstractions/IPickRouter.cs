namespace MintPlayer.Spark.Warehouse;

/// <summary>The locations one trip visits, to be put in visiting order.</summary>
/// <param name="ZoneId">The zone of a zone-picking trip, or null.</param>
/// <param name="Start">Where the picker starts (a staging or depot location), or null.</param>
/// <param name="End">Where the trip ends (a pack station, put wall or consolidation slot), or null.</param>
/// <param name="Locations">The distinct locations to visit.</param>
public sealed record PickRouteRequest(
    string SiteId,
    string? ZoneId,
    WarehouseLocationRef? Start,
    WarehouseLocationRef? End,
    IReadOnlyList<WarehouseLocationRef> Locations);

/// <summary>
/// Puts a trip's locations in visiting order (PRD §4.5 Locations, "Routing by walk sequence"; §4.7: tasks are sequenced
/// by walk sequence, per zone for zone picking). The built-in, <see cref="WarehouseStrategyNames.WalkSequence"/>, sorts by
/// <see cref="WarehouseLocationRef.WalkSequence"/>. A host router can use <see cref="WarehouseLocationRef.Geometry"/>
/// instead, e.g. for a largest-gap heuristic.
/// </summary>
public interface IPickRouter
{
    /// <summary>The name a site's configuration selects this router by.</summary>
    string Name { get; }

    /// <summary>Every location of the request exactly once, in visiting order.</summary>
    ValueTask<IReadOnlyList<WarehouseLocationRef>> SequenceAsync(PickRouteRequest request, CancellationToken cancellationToken = default);
}
