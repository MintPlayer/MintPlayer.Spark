namespace MintPlayer.Spark.Abstractions.Search;

/// <summary>
/// A search engine behind Spark search (<c>docs/spark_search_PRD.md</c> §4.1, D2): the RavenDB index by default, an
/// external engine when the application opts in. A provider only <b>finds candidates</b>. What the caller is shown is
/// decided by Spark afterwards: every candidate is re-read through the query path, with the type's <c>Query</c> right,
/// row security and redaction, and the label is rendered from that row. So a provider may search an index that knows
/// nothing about security, and a candidate the caller may not read simply disappears.
/// </summary>
public interface ISparkSearchProvider
{
    /// <summary>
    /// Candidates for <paramref name="request"/>, best first. Return more than <see cref="SparkSearchRequest.Take"/>
    /// when you can: candidates the caller may not read are dropped afterwards.
    /// </summary>
    Task<IReadOnlyList<SparkSearchCandidate>> SearchAsync(SparkSearchRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// The ids of the objects of <paramref name="entityTypes"/> whose label is exactly <paramref name="label"/>
    /// (ordinal, ignoring case), in any order. Used to make suggestion strings unambiguous and to resolve a picked one.
    /// </summary>
    Task<IReadOnlyList<SparkSearchCandidate>> FindByLabelAsync(string label, IReadOnlyCollection<string> entityTypes, CancellationToken cancellationToken);
}

/// <summary>One search.</summary>
/// <param name="Term">What the user typed, trimmed.</param>
/// <param name="EntityTypes">The names of the searchable types the caller may query; never empty.</param>
/// <param name="Take">How many hits the caller wants after filtering.</param>
/// <param name="Prefix">Whether the last word may be incomplete (suggestions as you type).</param>
/// <param name="FuzzyTypes">The subset of <paramref name="EntityTypes"/> for which typos are tolerated.</param>
public sealed record SparkSearchRequest(
    string Term,
    IReadOnlyCollection<string> EntityTypes,
    int Take,
    bool Prefix,
    IReadOnlyCollection<string> FuzzyTypes);

/// <summary>A document a provider found: its entity type name (the model's <c>name</c>) and id.</summary>
public sealed record SparkSearchCandidate(string EntityType, string Id);
