namespace MintPlayer.Spark.Search;

/// <summary>
/// Spark search (<c>docs/spark_search_PRD.md</c>, #238): suggestions and results across every entity type whose model
/// has <c>search.enabled</c>, as the current caller may see them. Used by the search endpoints and by
/// MintPlayer.Spark.OpenSearchDescription.
/// </summary>
public interface ISparkSearch
{
    /// <summary>
    /// Suggestions for a term being typed (the last word may be incomplete). Every label is unique among the objects the
    /// caller can see, so a picked one can be resolved back with <see cref="ResolveAsync"/>.
    /// </summary>
    Task<IReadOnlyList<SparkSearchHit>> SuggestAsync(string? term, CancellationToken cancellationToken = default);

    /// <summary>The results of a completed term, best first, with the same unambiguous labels.</summary>
    Task<IReadOnlyList<SparkSearchHit>> SearchAsync(string? term, int? take = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// The one object <paramref name="text"/> names: a label <see cref="SuggestAsync"/> produced, or a term that matches
    /// exactly one visible object. Null when it names none or several.
    /// </summary>
    Task<SparkSearchHit?> ResolveAsync(string? text, CancellationToken cancellationToken = default);
}

/// <summary>One visible hit.</summary>
/// <param name="EntityType">The model's entity type name.</param>
/// <param name="TypeLabel">The type's label in the request's language.</param>
/// <param name="Id">The document id.</param>
/// <param name="Label">The unambiguous display string (D3).</param>
/// <param name="Url">The object's page, an absolute path (<c>/po/:type/:id/:slug</c>).</param>
public sealed record SparkSearchHit(string EntityType, string TypeLabel, string Id, string Label, string Url);
