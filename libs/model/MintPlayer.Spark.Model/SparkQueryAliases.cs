namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Derives query aliases — <b>one query per URL</b>. Indexing queries by alias is
/// <c>SparkQueryAliasIndex</c> in MintPlayer.Spark.Abstractions, which uses this same derivation.
/// </summary>
/// <remarks>
/// In the Model package (#388) so a package that declares queries without the Web-SDK Abstractions
/// (Contributions.Abstractions) derives the same alias the application resolves. Written twice,
/// they would disagree the first time either changed.
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
}
