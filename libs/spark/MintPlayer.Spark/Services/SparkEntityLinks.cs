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
        => ListQuery(queries, type.Name, q => q.EntityType, q => q.Source);

    /// <summary>
    /// <see cref="ListQuery(EntityTypeDefinition, IEnumerable{SparkQuery})"/> over any query shape. A <c>Database.</c>
    /// source wins over a <c>Custom.</c> one, in file order otherwise: a custom query may be a sub-query that needs a
    /// parent (mintplayer.com, 2026-11-05: Song's <c>Custom.Artist_Songs</c> came first and every song hit threw).
    /// A custom query is still the list when it is the only match.
    /// </summary>
    internal static T? ListQuery<T>(IEnumerable<T> queries, string typeName, Func<T, string?> entityType, Func<T, string?> source)
        where T : class
        => queries
            .Where(q =>
            {
                if (entityType(q) == typeName) return true;
                var text = source(q) ?? "";
                var sourceName = text.Contains('.') ? text[(text.IndexOf('.') + 1)..] : text;
                return sourceName == typeName || sourceName == typeName + "s";
            })
            .OrderBy(q => (source(q) ?? "").StartsWith("Custom.", StringComparison.Ordinal) ? 1 : 0)
            .FirstOrDefault();

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
