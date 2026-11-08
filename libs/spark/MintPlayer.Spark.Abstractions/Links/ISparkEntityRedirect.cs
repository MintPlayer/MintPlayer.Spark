namespace MintPlayer.Spark.Abstractions.Links;

/// <summary>
/// A URL that names an object by a natural key instead of its id (#602, tier c), e.g. CodeCoverage's
/// <c>/github/r/{owner}/{repo}</c>. Not needed by an ordinary app: an object's own page is <c>po/{type}/{id}</c> or its
/// type's <see cref="EntityTypeDefinition.Route"/>.
/// <para>
/// The server answers a matching page request before Angular renders: it calls <see cref="ResolveAsync"/>, loads the
/// object through the normal read path, and answers <c>301</c> to its canonical page. The app writes only the lookup:
/// a <see langword="null"/> answer, a missing object and an object the caller may not read all get the same response
/// (404, or 401 for an anonymous caller), so the lookup needs no authorization of its own and never reveals whether a
/// private object exists. Register with <c>AddEntityRedirect&lt;T&gt;()</c>; it is resolved per request (scoped).
/// </para>
/// </summary>
public interface ISparkEntityRedirect
{
    /// <summary>
    /// The path pattern, without a leading slash: literal segments and <c>{name}</c> placeholders, each one whole
    /// segment, e.g. <c>{provider}/r/{owner}/{repo}</c>. Literals compare case-insensitively.
    /// </summary>
    string Pattern { get; }

    /// <summary>The object the matched values name, or <see langword="null"/> when there is none.</summary>
    /// <param name="values">The placeholders' values, decoded, by name.</param>
    Task<SparkEntityTarget?> ResolveAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken);
}

/// <summary>An object a <see cref="ISparkEntityRedirect"/> names.</summary>
/// <param name="Type">The entity type: its name, alias or id.</param>
/// <param name="Id">The object id.</param>
public sealed record SparkEntityTarget(string Type, string Id);
