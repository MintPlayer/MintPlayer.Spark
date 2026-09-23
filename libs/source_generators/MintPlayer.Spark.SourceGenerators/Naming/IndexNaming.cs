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

    /// <summary>
    /// The context query-root member name, e.g. <c>Car</c> to <c>VCars</c> and <c>Person</c> to <c>VPeople</c>.
    /// <para>Pluralizes the <em>entity</em> name and re-prefixes, rather than pluralizing the index-entity
    /// name: the irregular table keys on real words, so <c>Pluralize("VPerson")</c> would yield
    /// <c>VPersons</c> where the hand-written root is <c>VPeople</c>. A custom
    /// <c>IndexEntityName</c> is pluralized directly, since no entity name underlies it.</para>
    /// </summary>
    public static string ContextRoot(string entityName, string indexEntityName)
        => indexEntityName == IndexEntityName(entityName)
            ? $"V{Pluralize(entityName)}"
            : Pluralize(indexEntityName);

    /// <summary>The sort-companion name for a field. Suffix is <c>Sort</c>, with no separator.</summary>
    /// <remarks>
    /// ⚠️ Used for a <c>[Breadcrumb]</c> companion only. A <c>[Search]</c> field no longer gets one —
    /// see <see cref="SearchCompanion"/> for why the roles were swapped.
    /// </remarks>
    public static string SortCompanion(string propertyName) => $"{propertyName}Sort";

    /// <summary>The search-companion name for a field. Suffix is <c>Search</c>, with no separator.</summary>
    /// <remarks>
    /// ⚠️ <b>This is the field that carries <c>FieldIndexing.Search</c>, not the base field.</b> The
    /// two used to be the other way round, and it put the abnormality on the field everyone names:
    /// analyzing a field destroys equality and ordering on it, so <c>x.FullName == value</c> matched
    /// nothing and the usable value was exiled to <c>FullNameSort</c>. Every filter and sort had to be
    /// silently redirected there, and anything that did not go through the redirect — a hand-written
    /// row filter, a custom query building rows by hand — got the broken field.
    /// <para>
    /// The <c>DateTimeOffset</c> mechanism already worked the right way round (plain base field,
    /// abnormality on the <c>{Name}Raw</c> companion), so this makes the two agree. The base field is
    /// now a normal indexed field and only <c>ApplySearch</c> knows the companion exists.
    /// </para>
    /// </remarks>
    public static string SearchCompanion(string propertyName) => $"{propertyName}Search";

    /// <summary>
    /// The wrapper-companion name for a field. Suffix is <c>Raw</c>, with no separator.
    /// <para>Must stay in lockstep with <c>ProjectedOffsetRestorer.WrapperSuffix</c>, which resolves the
    /// companion at runtime by this same convention.</para>
    /// </summary>
    public static string WrapperCompanion(string propertyName) => $"{propertyName}Raw";

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
