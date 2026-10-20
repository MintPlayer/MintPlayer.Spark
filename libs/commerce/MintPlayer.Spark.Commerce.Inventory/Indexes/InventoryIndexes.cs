using System.Linq.Expressions;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Commerce.Inventory.Indexes;

/// <summary>
/// The engine's index declarations (X19): the application's generator emits the index classes, so indexes live in the
/// application (owner rule) and every host gets the same ones. Queried by name.
/// <list type="table">
/// <item><term><c>InventoryClaims/Lapsing</c></term><description>Claims by state and expiry: the sweeper's "held and
/// lapsed" query (<see cref="InventoryEngine.ReleaseLapsedAsync"/>). A stale result only delays a release (I3).</description></item>
/// </list>
/// </summary>
public static class InventoryIndexes
{
    /// <summary>The deployed name of the sweeper's index.</summary>
    public const string Lapsing = "InventoryClaims/Lapsing";

    [SparkIndexMap("InventoryClaims_Lapsing")]
    public static Expression<Func<IEnumerable<InventoryClaim>, IEnumerable<LapsingClaimEntry>>> LapsingClaims { get; } =
        claims => from claim in claims
                  select new LapsingClaimEntry { State = claim.State, ExpiresAt = claim.ExpiresAt };
}

/// <summary>One entry of <see cref="InventoryIndexes.Lapsing"/>.</summary>
public sealed class LapsingClaimEntry
{
    public ClaimState State { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}
