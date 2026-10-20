namespace MintPlayer.Spark.LibraryGenerators.Generators;

/// <summary>
/// The default <c>TargetCollection</c> of a <c>[Replicated]</c> class: its name pluralised, close to
/// RavenDB's default conventions (Inflector).
/// </summary>
/// <remarks>
/// Deliberately small: irregulars, consonant + <c>y</c> → <c>ies</c>, <c>s/x/z/ch/sh</c> → <c>es</c>, else
/// <c>s</c>. It is a default, not the truth: the consumer's document store decides the collection, and
/// startup verifies the generated <c>ReplicationTargetCollection</c> equals it, so a class whose name
/// Inflector pluralises differently fails loudly there and sets <c>TargetCollection</c> explicitly.
/// </remarks>
internal static class ReplicaCollectionNames
{
    // Matched as a suffix, as Inflector's irregular rules are ("SalesPerson" → "SalesPeople").
    private static readonly (string Singular, string Plural)[] Irregulars =
    [
        ("person", "people"),
        ("woman", "women"),
        ("man", "men"),
        ("child", "children"),
        ("mouse", "mice"),
        ("goose", "geese"),
        ("foot", "feet"),
        ("tooth", "teeth"),
    ];

    public static string Pluralize(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;

        foreach (var (singular, plural) in Irregulars)
        {
            if (name.Length >= singular.Length && name.EndsWith(singular, StringComparison.OrdinalIgnoreCase))
            {
                var start = name.Length - singular.Length;
                // Keep the casing of the replaced word's first letter: "Person" → "People", "person" → "people".
                var first = char.IsUpper(name[start]) ? char.ToUpperInvariant(plural[0]) : plural[0];
                return name.Substring(0, start) + first + plural.Substring(1);
            }
        }

        var last = char.ToLowerInvariant(name[name.Length - 1]);
        if (last == 'y' && name.Length > 1 && !IsVowel(char.ToLowerInvariant(name[name.Length - 2])))
            return name.Substring(0, name.Length - 1) + "ies";

        if (last is 's' or 'x' or 'z'
            || name.EndsWith("ch", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("sh", StringComparison.OrdinalIgnoreCase))
            return name + "es";

        return name + "s";
    }

    private static bool IsVowel(char c) => c is 'a' or 'e' or 'i' or 'o' or 'u';
}
