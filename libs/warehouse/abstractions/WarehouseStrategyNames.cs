namespace MintPlayer.Spark.Warehouse;

/// <summary>
/// The names of the built-in strategies, as configuration refers to them (PRD §4.13: <c>Spark:Warehouse:Allocation:Rule</c>
/// is <c>Fifo</c> | <c>Fefo</c> | <c>ClosestLocation</c> | custom). A custom strategy picks any other name.
/// </summary>
public static class WarehouseStrategyNames
{
    /// <summary>Allocation (WD4): oldest receipt first.</summary>
    public const string Fifo = "Fifo";

    /// <summary>Allocation (WD4): earliest removal date (expiry minus the item's minimum shelf life) first.</summary>
    public const string Fefo = "Fefo";

    /// <summary>Allocation (WD4): lowest walk sequence first.</summary>
    public const string ClosestLocation = "ClosestLocation";

    /// <summary>Pick work (WD5): one order per trip.</summary>
    public const string Discrete = "Discrete";

    /// <summary>Pick work (WD5): several orders picked by article, sorted at a put wall.</summary>
    public const string Batch = "Batch";

    /// <summary>Pick work (WD5): one tote per order on a cart, sorted while picking.</summary>
    public const string Cluster = "Cluster";

    /// <summary>Pick work (WD5): pickers per zone; the order is consolidated afterwards.</summary>
    public const string Zone = "Zone";

    /// <summary>Routing (§4.5): sort by walk sequence.</summary>
    public const string WalkSequence = "WalkSequence";

    /// <summary>Cartonisation (§4.7): the packer chooses; no suggestion.</summary>
    public const string Manual = "Manual";

    /// <summary>Cartonisation (§4.7): the smallest carton whose volume and weight limits fit.</summary>
    public const string Volumetric = "Volumetric";
}
