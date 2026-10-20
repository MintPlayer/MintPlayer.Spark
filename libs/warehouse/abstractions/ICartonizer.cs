namespace MintPlayer.Spark.Warehouse;

/// <summary>
/// A carton the pack station can use: a snapshot of a carton-type document (PRD §4.5). Not named <c>CartonType</c>,
/// because that is the document's name and entity names must be unique across loaded assemblies (§4.1, Names).
/// </summary>
/// <param name="InnerDimensions">The usable inside, in millimetres.</param>
/// <param name="MaxWeightGrams">The maximum gross weight, carton included.</param>
/// <param name="TareGrams">The empty carton's weight.</param>
public sealed record CartonOption(string CartonTypeId, string Code, Dimensions InnerDimensions, decimal MaxWeightGrams, decimal TareGrams);

/// <summary>Units of one item to be packed.</summary>
/// <param name="Quantity">In base units.</param>
/// <param name="UnitDimensions">One unit's outer dimensions, or null when the item has none on file.</param>
/// <param name="UnitWeightGrams">One unit's weight, or null when unknown.</param>
/// <param name="DangerousGoodsClass">The item's dangerous-goods class (commerce GC9), or null. A cartoniser may keep classes apart.</param>
public sealed record CartonizationItem(
    string ItemId,
    decimal Quantity,
    Dimensions? UnitDimensions,
    decimal? UnitWeightGrams,
    string? DangerousGoodsClass = null);

/// <summary>What is being packed, and the cartons available at the station.</summary>
public sealed record CartonizationRequest(
    string SiteId,
    string OwnerId,
    string OrderId,
    IReadOnlyList<CartonizationItem> Items,
    IReadOnlyList<CartonOption> Cartons);

/// <summary>One suggested carton and what goes in it.</summary>
/// <param name="EstimatedWeightGrams">Contents plus tare, from the item data; the weighing step records the real weight.</param>
public sealed record CartonAssignment(CartonOption Carton, IReadOnlyList<CartonizationItem> Contents, decimal EstimatedWeightGrams);

/// <summary>A suggestion. The packer can always override it.</summary>
/// <param name="Unplaced">Units the cartoniser could not place (no dimensions on file, or too large for every carton); the packer handles them.</param>
public sealed record CartonizationResult(IReadOnlyList<CartonAssignment> Cartons, IReadOnlyList<CartonizationItem> Unplaced)
{
    /// <summary>No suggestion: the packer chooses (the <see cref="WarehouseStrategyNames.Manual"/> cartoniser).</summary>
    public static CartonizationResult None(IReadOnlyList<CartonizationItem> items) => new([], items);
}

/// <summary>
/// Suggests cartons at the pack station (PRD §4.7 Packing, step 3). The built-ins are
/// <see cref="WarehouseStrategyNames.Manual"/> and <see cref="WarehouseStrategyNames.Volumetric"/> (the smallest carton
/// type whose volume and weight limits fit). 3D packing is not built in (Q-W22); a host plugs it in here.
/// </summary>
public interface ICartonizer
{
    /// <summary>The name a site's configuration selects this cartoniser by.</summary>
    string Name { get; }

    ValueTask<CartonizationResult> SuggestAsync(CartonizationRequest request, CancellationToken cancellationToken = default);
}
