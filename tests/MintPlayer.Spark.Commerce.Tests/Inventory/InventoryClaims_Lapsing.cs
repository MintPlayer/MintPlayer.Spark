using System.Collections;
using System.Linq.Expressions;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Inventory.Indexes;
using Raven.Client.Documents.Indexes;

namespace MintPlayer.Spark.Commerce.Tests.Inventory;

/// <summary>
/// The sweeper's declared index (X19) for tests: this project does not run the application's source generator, so the
/// index is built here from the library's own map expression, under the name the generator would give it
/// (<c>InventoryClaims/Lapsing</c>). The map is the library's, not a copy, so a change to it is tested.
/// </summary>
public sealed class InventoryClaims_Lapsing : AbstractMultiMapIndexCreationTask<LapsingClaimEntry>
{
    public InventoryClaims_Lapsing()
    {
        var map = InventoryIndexes.LapsingClaims;
        AddMap(Expression.Lambda<Func<IEnumerable<InventoryClaim>, IEnumerable>>(map.Body, map.Parameters));
    }
}
