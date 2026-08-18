using System.Collections.Generic;

namespace MintPlayer.Spark.SourceGenerators.Naming;

/// <summary>
/// The single place index, index-entity and map-variable names are derived.
/// <para>
/// Kept as one small explicit function on purpose. In the design this replaces, name derivation was
/// spread across two independent traversals that re-derived it separately — the classic source of
/// "the index and the context disagree" bugs — and leaned on a general-purpose inflector that needed a
/// hack for its own inputs.
/// </para>
/// <para><strong>Naming is not cosmetic here: the RavenDB index name is the CLR class name, so changing
/// it discards the deployed index and rebuilds from scratch.</strong> That is why pluralization is a short
/// predictable rule plus a handful of irregulars rather than a clever library, and why
/// <c>[GenerateIndex(IndexName = "...")]</c> exists as the escape hatch for anything it gets wrong.</para>
/// </summary>
internal static class IndexNaming
{
    /// <summary>
    /// Irregular plurals worth knowing, because they appear in this repo's own entities.
    /// <c>Person</c> is the load-bearing one: the hand-written indexes are named <c>People_Overview</c>,
    /// so a rule-only pluralizer would silently propose renaming a deployed index.
    /// </summary>
    private static readonly Dictionary<string, string> Irregular = new()
    {
        ["person"] = "People",
        ["child"] = "Children",
        ["man"] = "Men",
        ["woman"] = "Women",
        ["tooth"] = "Teeth",
        ["foot"] = "Feet",
        ["mouse"] = "Mice",
        ["goose"] = "Geese",
    };

    /// <summary>Default index name for an entity: <c>{Plural}_Overview</c>.</summary>
    public static string IndexName(string entityName) => $"{Pluralize(entityName)}_Overview";

    /// <summary>Default index-entity name for an entity: <c>V{EntityName}</c>.</summary>
    public static string IndexEntityName(string entityName) => $"V{entityName}";

    /// <summary>Lambda parameter for the document collection, e.g. <c>Car</c> to <c>cars</c>.</summary>
    public static string CollectionVariable(string entityName) => CamelCase(Pluralize(entityName));

    /// <summary>Range variable for one document, e.g. <c>Car</c> to <c>car</c>.</summary>
    public static string ItemVariable(string entityName) => CamelCase(entityName);

    /// <summary>The sort-companion name for a field. Suffix is <c>Sort</c>, with no separator.</summary>
    public static string SortCompanion(string propertyName) => $"{propertyName}Sort";

    /// <summary>A per-language flattened field name, e.g. <c>Description</c> + <c>nl</c>.</summary>
    public static string LanguageField(string propertyName, string language) => $"{propertyName}_{language}";

    public static string Pluralize(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;

        if (Irregular.TryGetValue(name.ToLowerInvariant(), out var irregular))
            return irregular;

        if (name.EndsWith("s") || name.EndsWith("x") || name.EndsWith("z")
            || name.EndsWith("ch") || name.EndsWith("sh"))
            return name + "es";

        if (name.Length > 1 && name.EndsWith("y") && !IsVowel(name[name.Length - 2]))
            return name.Substring(0, name.Length - 1) + "ies";

        return name + "s";
    }

    private static bool IsVowel(char c) => "aeiouAEIOU".IndexOf(c) >= 0;

    private static string CamelCase(string name)
        => string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);
}
