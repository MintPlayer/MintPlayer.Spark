namespace MintPlayer.Spark.Commerce.Inventory;

// The engine's documents. Only the engine writes them, and only in cluster-wide sessions (S18): a cluster-wide
// transaction refuses a document an ordinary session wrote meanwhile, so a Spark CRUD write, a patch or a counter
// would break the next claim. They are therefore no persistent-object types (startup refuses one that maps them),
// and their collections keep revisions off. The names carry the "Inventory" prefix because the same CLR simple name
// in two libraries silently shares a collection (S0).

/// <summary>A sellable capacity (D15), split over <see cref="ShardCount"/> shard documents so claims on it don't all contend for one document.</summary>
public sealed class InventoryQuota
{
    public string? Id { get; set; }
    public int Capacity { get; set; }
    public int ShardCount { get; set; }
}

/// <summary>
/// One shard of a quota: <c>{quotaId}/shards/{k}</c>. Holds are kept inline; expired ones are pruned by the
/// next write that touches the shard, so expiry stays lazy (I3).
/// </summary>
public sealed class InventoryQuotaShard
{
    public string? Id { get; set; }
    public string QuotaId { get; set; } = "";
    public int CapacityShare { get; set; }
    public int Sold { get; set; }
    public List<Hold> Holds { get; set; } = [];
}

/// <summary>A hold of <see cref="N"/> units for a claim. Live until <c>ExpiresAt + grace</c> (I5).</summary>
public sealed class Hold
{
    public string ClaimId { get; set; } = "";
    public int N { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>
/// A sellable item's quota mapping (D15): <c>inventory-items/{itemId}</c>. The item sells only if every quota it
/// counts against has room for <c>N × multiplier</c>.
/// </summary>
public sealed class InventoryItem
{
    public string? Id { get; set; }
    public string ItemId { get; set; } = "";
    public List<ItemQuotaMapping> Quotas { get; set; } = [];
}

public sealed class ItemQuotaMapping
{
    public string QuotaId { get; set; } = "";
    public int Multiplier { get; set; } = 1;
}

/// <summary>
/// A fill chain (D25): seats in the organiser's fill order, row by row. Chain fill seats a group in the smallest hole
/// before the cursor that fits, else in the next run from the cursor. The chain is one document, so it is the hot key
/// of its seats; one chain per block group is the contention lever (§4.3).
/// </summary>
public sealed class InventoryChain
{
    public string? Id { get; set; }

    /// <summary>The rows in fill order; each row's units in fill order. A group never spans two rows.</summary>
    public List<List<string>> Rows { get; set; } = [];

    /// <summary>The flat position (over all rows) from which every seat is untouched.</summary>
    public int Cursor { get; set; }

    /// <summary>Free positions before <see cref="Cursor"/>, ascending: released seats and seats skipped at a row's end.</summary>
    public List<int> Holes { get; set; } = [];
}

/// <summary>The compare-exchange value of a per-buyer limit key: units sold to the buyer plus live holds.</summary>
public sealed class BuyerCount
{
    public int Count { get; set; }
    public List<Hold> Holds { get; set; } = [];
}

public enum ClaimState { Held, Converted, Released }

/// <summary>A claim document: what one basket or order holds, and exactly where, so every return path gives it back once (I4).</summary>
public sealed class InventoryClaim
{
    public string? Id { get; set; }
    public string Owner { get; set; } = "";
    public ClaimState State { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>The items the claim was made for (what the request named, before mapping).</summary>
    public List<ClaimItem> Items { get; set; } = [];

    /// <summary>Quota units by shard. Never changed by a return: <see cref="Returns"/> records what came back.</summary>
    public List<ShardAllocation> Allocations { get; set; } = [];

    /// <summary>Every numbered unit, explicit, picked or chain-filled.</summary>
    public List<string> Units { get; set; } = [];

    /// <summary>The units chain fill chose, with their chain positions (a release gives the positions back as holes).</summary>
    public List<ChainAllocation> Chains { get; set; } = [];

    public List<LimitAllocation> Limits { get; set; } = [];

    /// <summary>Returns from the converted claim, each exactly once (I4).</summary>
    public List<ClaimReturn> Returns { get; set; } = [];

    /// <summary>Why the claim was released as a whole, once it is.</summary>
    public ReturnReason? ReleaseReason { get; set; }
}

public sealed class ClaimItem
{
    public string ItemId { get; set; } = "";
    public int N { get; set; }
}

public sealed class ShardAllocation
{
    public string QuotaId { get; set; } = "";
    public string ShardId { get; set; } = "";
    public int N { get; set; }
}

public sealed class ChainAllocation
{
    public string ChainId { get; set; } = "";
    public List<int> Positions { get; set; } = [];
    public List<string> Units { get; set; } = [];
}

public sealed class LimitAllocation
{
    public string Key { get; set; } = "";
    public int N { get; set; }
}

/// <summary>One return from a converted claim (I4), keyed by its id.</summary>
public sealed class ClaimReturn
{
    public string Id { get; set; } = "";
    public ReturnReason Reason { get; set; }
    public DateTimeOffset At { get; set; }
    public List<ShardAllocation> Allocations { get; set; } = [];
    public List<string> Units { get; set; } = [];
    public List<LimitAllocation> Limits { get; set; } = [];

    /// <summary>Whether the quota units went back to stock. False for shipped goods until they are restocked.</summary>
    public bool StockReturned { get; set; }
}
