using Microsoft.CodeAnalysis;
using System.Linq;

namespace MintPlayer.Spark.SourceGenerators.Models;

/// <summary>
/// Symbol-level counterparts to the runtime reflection rules in
/// <c>MintPlayer.Spark.Abstractions.Reflection.ReflectedTypeExtensions</c>. Generators and
/// analyzers see Roslyn symbols rather than <c>PropertyInfo</c>, so the model rules have to be
/// restated here — keep the two in step.
/// </summary>
internal static class SparkModelSymbols
{
    private const string IgnorePropertyAttributeFullName =
        "MintPlayer.Spark.Abstractions.IgnorePropertyAttribute";

    private const string IgnoreForIndexAttributeFullName =
        "MintPlayer.Spark.Abstractions.IgnoreForIndexAttribute";

    private const string SearchAttributeFullName =
        "MintPlayer.Spark.Abstractions.SearchAttribute";

    private const string GenerateIndexAttributeFullName =
        "MintPlayer.Spark.Abstractions.GenerateIndexAttribute";

    private const string TranslatedStringFullName =
        "MintPlayer.Spark.Abstractions.TranslatedString";

    /// <summary>
    /// Whether <paramref name="property"/> carries <c>[IgnoreProperty]</c> and is therefore not
    /// part of the Spark model. Matched on the fully-qualified attribute name so the check does
    /// not depend on the compilation having a reference symbol available.
    /// </summary>
    public static bool IsIgnoredForSparkModel(this IPropertySymbol property)
        => property.HasAttribute(IgnorePropertyAttributeFullName);

    /// <summary>
    /// Whether <paramref name="property"/> carries <c>[IgnoreForIndex]</c> — excluded from a generated
    /// index and projection, but still a full member of the Spark model.
    /// </summary>
    public static bool IsIgnoredForIndex(this IPropertySymbol property)
        => property.HasAttribute(IgnoreForIndexAttributeFullName);

    /// <summary>
    /// Whether <paramref name="property"/> carries <c>[Search]</c>, meaning it is indexed for full-text
    /// search and gets a <c>{Name}Sort</c> companion.
    /// </summary>
    public static bool IsSearchable(this IPropertySymbol property)
        => property.HasAttribute(SearchAttributeFullName);

    /// <summary>
    /// Whether <paramref name="type"/> carries <c>[GenerateIndex]</c>. Works for types from source and
    /// from referenced assemblies alike, since both expose attributes as symbols.
    /// </summary>
    public static bool HasGenerateIndex(this INamedTypeSymbol type)
        => type.GetAttributes().Any(a =>
            a.AttributeClass?.ToDisplayString() == GenerateIndexAttributeFullName);

    /// <summary>
    /// Symbol-level twin of <c>ReflectedTypeExtensions.IsSparkModelProperty</c>: a readable, public,
    /// non-static, non-indexer property other than <c>Id</c> that is not <c>[IgnoreProperty]</c>.
    /// </summary>
    public static bool IsSparkModelProperty(this IPropertySymbol property)
        => property.Name != "Id"
        && property.GetMethod is not null
        && property.Parameters.IsEmpty
        && !property.IsStatic
        && property.DeclaredAccessibility == Accessibility.Public
        && !property.IsIgnoredForSparkModel();

    /// <summary>
    /// Whether a generated index should map <paramref name="property"/>: a Spark model property that is
    /// not additionally excluded by <c>[IgnoreForIndex]</c>.
    /// <para>Divergence between this and <c>IsSparkModelProperty</c> makes the generated index and the
    /// committed model hash disagree, so the two must stay in step.</para>
    /// </summary>
    public static bool IsIndexableProperty(this IPropertySymbol property)
        => property.IsSparkModelProperty() && !property.IsIgnoredForIndex();

    /// <summary>
    /// Whether <paramref name="type"/> is <c>TranslatedString</c>, which serializes as a flat per-language
    /// JSON object rather than as a nested dictionary and therefore cannot be indexed by its CLR path.
    /// </summary>
    public static bool IsTranslatedString(this ITypeSymbol? type)
        => type?.ToDisplayString() == TranslatedStringFullName;

    /// <summary>
    /// Every indexable property on <paramref name="type"/> and its base types, most-derived first, with
    /// members hidden by a more-derived declaration of the same name reported once.
    /// <para>Walking the hierarchy is deliberate: discovering only declared members silently drops
    /// inherited properties, which is a documented defect of the design this replaces.</para>
    /// </summary>
    public static IEnumerable<IPropertySymbol> GetIndexableProperties(this INamedTypeSymbol type)
    {
        var seen = new HashSet<string>();
        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
            {
                if (!property.IsIndexableProperty()) continue;
                if (!seen.Add(property.Name)) continue;
                yield return property;
            }
        }
    }

    private static bool HasAttribute(this IPropertySymbol property, string fullName)
        => property.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == fullName);
}
