namespace MintPlayer.Spark.Commerce.Inventory;

/// <summary>A sellable capacity, split over <see cref="ShardCount"/> shard documents so claims on it don't all contend for one document.</summary>
public sealed class Quota
{
    public string? Id { get; set; }
    public int Capacity { get; set; }
    public int ShardCount { get; set; }
}

/// <summary>
/// One shard of a quota: <c>{quotaId}/shards/{k}</c>. Holds are kept inline; expired ones are pruned by the
/// next write that touches the shard, so expiry stays lazy (I3).
/// </summary>
public sealed class QuotaShard
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

/// <summary>The compare-exchange value of a numbered unit (a seat). <see cref="ExpiresAt"/> is null once sold.</summary>
public sealed class UnitHold
{
    public string ClaimId { get; set; } = "";
    public DateTimeOffset? ExpiresAt { get; set; }
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
    public List<ShardAllocation> Allocations { get; set; } = [];
    public List<string> Units { get; set; } = [];
    public List<LimitAllocation> Limits { get; set; } = [];
}

public sealed class ShardAllocation
{
    public string ShardId { get; set; } = "";
    public int N { get; set; }
}

public sealed class LimitAllocation
{
    public string Key { get; set; } = "";
    public int N { get; set; }
}
