namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Derives query aliases and indexes them by name — <b>one query per URL</b>.
/// </summary>
/// <remarks>
/// Shared by the runtime loader and the build-time <c>--spark-verify-model</c> gate, so the set of
/// aliases CI accepts is provably the set the application resolves. Written twice, they would
/// disagree the first time either changed.
/// </remarks>
public static class SparkQueryAliases
{
    /// <summary>
    /// The alias a query is reachable at when it declares none: the name, minus a <c>Get</c>
    /// prefix, lowercased. <c>GetCars</c> → <c>cars</c>.
    /// </summary>
    public static string Derive(string name)
    {
        var alias = name;
        if (alias.StartsWith("Get", StringComparison.OrdinalIgnoreCase) && alias.Length > 3)
            alias = alias[3..];
        return alias.ToLowerInvariant();
    }

    /// <summary>
    /// Assigns each query its alias (deriving one where absent) and indexes them.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Two queries resolve to the same alias. This is refused rather than warned about, because a
    /// URL cannot mean two things: <c>/query/{alias}</c>, <c>/spark/queries/{alias}</c>, its
    /// <c>/execute</c> and its <c>/stream</c> all take the same alias, so the losing query would
    /// be unreachable by name with nothing to say so at the point of use.
    /// <para>
    /// It used to be a <c>Console.WriteLine</c>, and a test pinned first-wins as though intended.
    /// DemoApp is what it cost: <c>GetStocks</c> (<c>Database.Stocks</c>, a collection nothing
    /// writes) and <c>StreamStocks</c> (the live grid its menu points at) both resolved to
    /// <c>stocks</c>, so the page rendered an empty grid and the streaming query was unreachable.
    /// </para>
    /// </exception>
    public static Dictionary<string, SparkQuery> Index(IEnumerable<SparkQuery> queries)
    {
        var byAlias = new Dictionary<string, SparkQuery>(StringComparer.OrdinalIgnoreCase);

        foreach (var query in queries)
        {
            query.Alias ??= Derive(query.Name);

            if (byAlias.TryGetValue(query.Alias, out var existing))
                throw new InvalidOperationException(DescribeCollision(query.Alias, existing, query));

            byAlias[query.Alias] = query;
        }

        return byAlias;
    }

    /// <summary>
    /// Names both sides and says which one <em>derived</em> its alias — that author does not know
    /// they chose one, and is usually the one who has to change.
    /// </summary>
    private static string DescribeCollision(string alias, SparkQuery first, SparkQuery second)
        => $"Two queries resolve to the alias '{alias}': {Describe(first)} and {Describe(second)}. "
         + "A URL identifies exactly one query, so the second would be unreachable by alias. Give "
         + "one of them an explicit, distinct \"alias\" in its model file.";

    private static string Describe(SparkQuery query)
        => $"'{query.Name}' (source {query.Source}, alias "
         + (string.Equals(query.Alias, Derive(query.Name), StringComparison.OrdinalIgnoreCase)
                ? "derived from its name)"
                : "declared)");
}
