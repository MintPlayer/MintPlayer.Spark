using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Prerendering;

namespace MintPlayer.Spark.Services;

/// <summary>
/// How a type's list and an object's page are found, shared by the sitemap and search
/// (<c>docs/spark_search_PRD.md</c> D12) so the two can never disagree with the <c>po/:type</c> page itself.
/// </summary>
internal static class SparkEntityLinks
{
    /// <summary>
    /// The type's list query, as <c>po/:type</c> finds it (<c>SparkRoutesPrerenderContributor.SupplyTypeListAsync</c>,
    /// mirroring spark-query-list.component.ts): the query of that entity type, else the one whose source names the
    /// type or its plural.
    /// </summary>
    internal static SparkQuery? ListQuery(EntityTypeDefinition type, IEnumerable<SparkQuery> queries)
        => queries.FirstOrDefault(q =>
        {
            if (q.EntityType == type.Name) return true;
            var source = q.Source ?? "";
            var sourceName = source.Contains('.') ? source[(source.IndexOf('.') + 1)..] : source;
            return sourceName == type.Name || sourceName == type.Name + "s";
        });

    /// <summary>The type's segment of a page URL, as the app's links write it: the alias, else the id.</summary>
    internal static string TypeSegment(EntityTypeDefinition type) => type.Alias ?? type.Id.ToString();

    /// <summary>
    /// The object's page path (spark-po-detail <c>writeSeo</c>): <c>/po/:type/:id/:slug</c>, without the slug when it is
    /// empty or "edit". <paramref name="typeSegment"/> is not encoded yet.
    /// </summary>
    internal static string DetailPath(string typeSegment, string id, string? breadcrumb)
    {
        var slug = SparkRoutesPrerenderContributor.SparkSlug(breadcrumb);
        var path = $"/po/{SparkRoutesPrerenderContributor.EncodeUriComponent(typeSegment)}/{SparkRoutesPrerenderContributor.EncodeUriComponent(id)}";
        return slug is "" or "edit" ? path : $"{path}/{slug}";
    }
}
