namespace MintPlayer.Spark.Warehouse;

/// <summary>What a warehouse location is used for (docs/warehouse_picking_PRD.md §4.5, <c>WarehouseLocation</c>).</summary>
public enum WarehouseLocationType
{
    Receiving,
    Storage,
    PickFace,
    Staging,
    Consolidation,
    PackStation,
    Quarantine,
    Returns,
}

/// <summary>
/// The physical address of a location. The warehouse routes by walk sequence, but keeps the geometry so that a host's
/// <see cref="IPickRouter"/> can use it (PRD §4.5, Locations: "Geometry stored anyway"). Every part is optional.
/// </summary>
public sealed record LocationGeometry(string? Aisle, string? Bay, string? Level, string? Position, string? Side);

/// <summary>
/// A location as the strategies see it: a read-only snapshot of a <c>WarehouseLocation</c> document (PRD §4.5).
/// </summary>
/// <param name="LocationId">The document id, <c>{siteId}/locations/{code}</c>.</param>
/// <param name="Code">The human code printed on the label.</param>
/// <param name="ZoneId">The zone, or null. Zone picking splits work per zone (§4.7).</param>
/// <param name="WalkSequence">The position on the walk path. Routing sorts by it (§4.5, "Routing by walk sequence").</param>
/// <param name="MaterializedPath">The parent chain, for hierarchical grouping (WX9), or null.</param>
public sealed record WarehouseLocationRef(
    string LocationId,
    string SiteId,
    string Code,
    string? ZoneId,
    long WalkSequence,
    WarehouseLocationType Type,
    LocationGeometry? Geometry = null,
    string? MaterializedPath = null);

/// <summary>Outer or inner dimensions in millimetres.</summary>
public sealed record Dimensions(decimal LengthMm, decimal WidthMm, decimal HeightMm)
{
    /// <summary>The volume in cubic millimetres.</summary>
    public decimal VolumeMm3 => LengthMm * WidthMm * HeightMm;
}

/// <summary>The packaging level a barcode or a quantity refers to (PRD §4.6: each, case, pallet).</summary>
public enum PackagingLevel
{
    Each,
    Case,
    Pallet,
}
