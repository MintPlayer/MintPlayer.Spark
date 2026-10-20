namespace MintPlayer.Spark.Warehouse.Outbound.Strategies;

/// <summary>
/// The built-in pick strategies (PRD §4.7 "Pick work", WD5). They only group the released lines into trips; the router
/// orders the visits and the release writes the work. Every line of the request lands in exactly one trip.
/// </summary>
internal static class PickTrips
{
    /// <summary>Orders in priority of appearance, so a wave's order (priority, then placed-at) carries into the trips.</summary>
    public static List<string> OrdersInOrder(IEnumerable<PickLine> lines) => lines.Select(l => l.OrderId).Distinct(StringComparer.Ordinal).ToList();

    public static IEnumerable<List<string>> Chunk(List<string> orders, int size) => orders.Chunk(Math.Max(1, size)).Select(c => c.ToList());

    public static PickTrip Trip(string? zoneId, PickSortation sortation, IReadOnlyCollection<string> orders, IEnumerable<PickLine> lines)
    {
        var set = orders.ToHashSet(StringComparer.Ordinal);
        return new PickTrip(zoneId, sortation, orders.ToList(), lines.Where(l => set.Contains(l.OrderId)).ToList());
    }
}

/// <summary>Discrete: one order per trip, nothing to sort (large orders).</summary>
internal sealed class DiscretePickStrategy : IPickStrategy
{
    public string Name => WarehouseStrategyNames.Discrete;

    public ValueTask<PickPlan> PlanAsync(PickPlanRequest request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new PickPlan(PickTrips.OrdersInOrder(request.Lines)
            .Select(o => PickTrips.Trip(null, PickSortation.None, [o], request.Lines))
            .ToList()));
}

/// <summary>Batch: several orders picked by article, then sorted at a put wall.</summary>
internal sealed class BatchPickStrategy(Microsoft.Extensions.Options.IOptions<SparkWarehouseOutboundOptions> options) : IPickStrategy
{
    public string Name => WarehouseStrategyNames.Batch;

    public ValueTask<PickPlan> PlanAsync(PickPlanRequest request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new PickPlan(PickTrips.Chunk(PickTrips.OrdersInOrder(request.Lines), options.Value.Picking.MaxOrdersPerTrip)
            .Select(chunk => PickTrips.Trip(null, PickSortation.PutWall, chunk, request.Lines))
            .ToList()));
}

/// <summary>Cluster: one tote per order on a cart, sorted while picking.</summary>
internal sealed class ClusterPickStrategy(Microsoft.Extensions.Options.IOptions<SparkWarehouseOutboundOptions> options) : IPickStrategy
{
    public string Name => WarehouseStrategyNames.Cluster;

    public ValueTask<PickPlan> PlanAsync(PickPlanRequest request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new PickPlan(PickTrips.Chunk(PickTrips.OrdersInOrder(request.Lines), options.Value.Picking.CartTotes)
            .Select(chunk => PickTrips.Trip(null, PickSortation.ToteOnCart, chunk, request.Lines))
            .ToList()));
}

/// <summary>
/// Zone: pickers per zone. Each order is split across the zones its lines lie in; per zone, orders are grouped in trips of
/// <c>MaxOrdersPerTrip</c>, and the zone totes are merged per order at a consolidation station (W4).
/// </summary>
internal sealed class ZonePickStrategy(Microsoft.Extensions.Options.IOptions<SparkWarehouseOutboundOptions> options) : IPickStrategy
{
    public string Name => WarehouseStrategyNames.Zone;

    public ValueTask<PickPlan> PlanAsync(PickPlanRequest request, CancellationToken cancellationToken = default)
    {
        var trips = new List<PickTrip>();
        foreach (var zone in request.Lines.GroupBy(l => l.Location.ZoneId ?? "", StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var zoneLines = zone.ToList();
            foreach (var chunk in PickTrips.Chunk(PickTrips.OrdersInOrder(zoneLines), options.Value.Picking.MaxOrdersPerTrip))
                trips.Add(PickTrips.Trip(zone.Key.Length == 0 ? null : zone.Key, PickSortation.Consolidation, chunk, zoneLines));
        }
        return ValueTask.FromResult(new PickPlan(trips));
    }
}

/// <summary>
/// Walk-sequence routing (PRD §4.5 "Routing by walk sequence"): visits sorted by walk sequence, ties by code. A host
/// router can use the geometry instead.
/// </summary>
internal sealed class WalkSequencePickRouter : IPickRouter
{
    public string Name => WarehouseStrategyNames.WalkSequence;

    public ValueTask<IReadOnlyList<WarehouseLocationRef>> SequenceAsync(PickRouteRequest request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<WarehouseLocationRef>>(Sequence(request.Locations));

    internal static List<WarehouseLocationRef> Sequence(IEnumerable<WarehouseLocationRef> locations)
        => locations
            .GroupBy(l => l.LocationId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(l => l.WalkSequence)
            .ThenBy(l => l.Code, StringComparer.Ordinal)
            .ToList();
}
