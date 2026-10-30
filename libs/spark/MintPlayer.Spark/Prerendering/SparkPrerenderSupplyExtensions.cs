using MintPlayer.Spark.Abstractions.Prerendering;

namespace MintPlayer.Spark.Prerendering;

/// <summary>
/// The supplies of Spark's own components, for a contributor that renders one inside its page (a board
/// tile, docs/spark_ssr_PRD.md SP5). None of them sets the page's status: the component is not the page's
/// primary read (D4), so a denied tile shows as denied and the page still answers 200.
/// </summary>
public static class SparkPrerenderSupplyExtensions
{
    /// <summary>What <c>&lt;spark-query-grid [queryId]&gt;</c> requests as a root grid: the query, its type's rights, actions and lookups, and the first page.</summary>
    public static Task SupplyQueryGridAsync(this ISparkPrerenderContext context, string queryId)
        => SparkRoutesPrerenderContributor.SupplyGridAsync(context, queryId, primary: false);

    /// <summary>What <c>&lt;spark-po-detail [objectType] [objectId]&gt;</c> requests: the object, its type's rights, actions and lookups, and its AsDetail references.</summary>
    public static Task SupplyPersistentObjectAsync(this ISparkPrerenderContext context, string objectType, string id)
        => SparkRoutesPrerenderContributor.SupplyObjectAsync(context, objectType, id, primary: false);
}
