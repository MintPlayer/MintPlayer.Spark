using MintPlayer.Spark.Warehouse.Models;

namespace MintPlayer.Spark.Warehouse.Outbound.Models;

/// <summary>The strategies' read-only view of a location document (Warehouse.Abstractions' <see cref="WarehouseLocationRef"/>).</summary>
public static class LocationRefs
{
    public static WarehouseLocationRef ToLocationRef(this WarehouseLocation location)
        => new(location.Id!, location.SiteId, location.Code, location.ZoneId, location.WalkSequence, location.Type,
            new LocationGeometry(location.Aisle, location.Bay, location.Level, location.Position, location.Side), location.Path);
}
