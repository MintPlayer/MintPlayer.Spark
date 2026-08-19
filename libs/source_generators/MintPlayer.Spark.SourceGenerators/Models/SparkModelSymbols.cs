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

    private const string SparkAbstractionsNamespace = "MintPlayer.Spark.Abstractions";

    private const string TranslatedStringTypeName = "TranslatedString";

    private const string FromIndexAttributeFullName =
        "MintPlayer.Spark.Abstractions.FromIndexAttribute";

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
    /// Whether <paramref name="type"/> is <c>DateTimeOffset</c> or <c>DateTimeOffset?</c>.
    /// <para><strong><c>DateTime</c> deliberately does not match.</strong> In the reference corpus every one
    /// of 15 <c>DateTimeOffset</c> properties is indexed <c>Exact</c> with a sort companion, and every one of
    /// 22 <c>DateTime</c> properties has neither. The asymmetry is intentional there and is reproduced here
    /// rather than "tidied up", because widening it would silently add fields to every existing index.</para>
    /// </summary>
    public static bool IsDateTimeOffset(this ITypeSymbol type)
        => type.UnwrapNullable().ToDisplayString() == "System.DateTimeOffset";

    /// <summary>
    /// The underlying type of a <c>Nullable&lt;T&gt;</c>, or the type itself. Reference-type nullability is
    /// an annotation rather than a wrapper, so this only affects value types.
    /// </summary>
    public static ITypeSymbol UnwrapNullable(this ITypeSymbol type)
        => type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } named
            ? named.TypeArguments[0]
            : type;

    /// <summary>
    /// Whether <paramref name="type"/> carries <c>[FromIndex]</c> and is therefore an index entity.
    /// </summary>
    public static bool HasFromIndex(this INamedTypeSymbol type)
        => type.GetAttributes().Any(a =>
            a.AttributeClass?.ToDisplayString() == FromIndexAttributeFullName);

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
    /// Whether <paramref name="type"/> is <c>TranslatedString</c>, which fans out into one index field per
    /// language instead of being indexed whole.
    /// <para>Its flat <c>{"en":..,"nl":..}</c> shape is a System.Text.Json concern and applies only on the
    /// wire. RavenDB persists it through Newtonsoft as <c>Description.Translations.nl</c>, so the CLR path
    /// <c>Description.Translations["nl"]</c> is what an index must map — measured, not assumed.</para>
    /// </summary>
    /// <remarks>
    /// Matched on name plus namespace rather than a rendered display string: <c>ToDisplayString()</c> includes
    /// the nullable annotation, so a <c>TranslatedString?</c> property renders with a trailing <c>?</c> and
    /// never equals the bare type name.
    /// </remarks>
    public static bool IsTranslatedString(this ITypeSymbol? type)
        => type is INamedTypeSymbol { Name: TranslatedStringTypeName } named
        && named.ContainingNamespace?.ToDisplayString() == SparkAbstractionsNamespace;

    /// <summary>
    /// Every indexable property on <paramref name="type"/> and its base types, most-derived first, with
    /// members hidden by a more-derived declaration of the same name reported once.
    /// <para>Walking the hierarchy is deliberate: discovering only declared members silently drops
    /// inherited properties, which is a documented defect of the design this replaces.</para>
    /// </summary>
    public static IEnumerable<IPropertySymbol> GetIndexableProperties(this INamedTypeSymbol type)
        => type.GetSparkProperties().Where(p => p.IsIndexableProperty());

    /// <summary>
    /// Every readable public instance property on <paramref name="type"/> and its base types, most-derived
    /// first, with a name hidden by a more-derived declaration reported once. No Spark filtering applied —
    /// callers that want the model or index rules apply them on top.
    /// </summary>
    public static IEnumerable<IPropertySymbol> GetSparkProperties(this INamedTypeSymbol type)
    {
        var seen = new HashSet<string>();
        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
            {
                if (property.IsStatic) continue;
                if (property.GetMethod is null) continue;
                if (!property.Parameters.IsEmpty) continue;
                if (property.DeclaredAccessibility != Accessibility.Public) continue;
                if (!seen.Add(property.Name)) continue;
                yield return property;
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="type"/> persists as a JSON object (or a collection of them) and would
    /// therefore fault Corax when indexed with default options — the fix is <c>FieldIndexing.No</c>.
    /// <para>Symbol-level twin of <c>SparkModelShape.IsComplexType</c>/<c>GetDataType</c>, keyed on the
    /// <em>serialized</em> shape rather than the CLR shape, which makes it deliberately stricter in two
    /// places: a user-defined struct is <c>IsValueType</c> but persists as a JSON object (complex here,
    /// scalar at runtime), and a dictionary persists as a JSON object even though its
    /// <c>KeyValuePair</c> element is a struct. The runtime rules must not be widened to match — they
    /// classify model columns, not index safety.</para>
    /// <para>Allow-list, so an unknown type degrades to complex: worst case a stored-but-unfilterable
    /// field, never a faulting index.</para>
    /// </summary>
    public static bool IsComplexForIndex(this ITypeSymbol type)
    {
        var current = type.UnwrapNullable();

        // Dictionaries persist as JSON objects; their KeyValuePair element must not be unwrapped
        // into a scalar verdict.
        if (IsDictionaryLike(current)) return true;

        if (GetCollectionElementType(current) is { } element)
            return element.UnwrapNullable() is var unwrapped
                && (IsDictionaryLike(unwrapped) || GetCollectionElementType(unwrapped) is not null || !IsScalarForIndex(unwrapped));

        return !IsScalarForIndex(current);
    }

    private static bool IsScalarForIndex(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Enum) return true;
        if (type.IsTranslatedString()) return true;

        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean:
            case SpecialType.System_Byte:
            case SpecialType.System_SByte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_Single:
            case SpecialType.System_Double:
            case SpecialType.System_Decimal:
            case SpecialType.System_Char:
            case SpecialType.System_String:
            case SpecialType.System_DateTime:
                return true;
        }

        return type.ToDisplayString() switch
        {
            "System.Guid" => true,
            "System.DateTimeOffset" => true,
            "System.TimeSpan" => true,
            "System.DateOnly" => true,
            "System.TimeOnly" => true,
            // Persisted as an "#rrggbb" string by ColorNewtonsoftJsonConverter — recursing into
            // R/G/B would be wrong, and inerting it would regress working Color columns.
            "System.Drawing.Color" => true,
            _ => false,
        };
    }

    private static bool IsDictionaryLike(ITypeSymbol type)
        => type is INamedTypeSymbol named
        && named.AllInterfaces.Concat(new[] { named })
            .Any(i => i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T
                && i.TypeArguments.Length == 1
                && i.TypeArguments[0].OriginalDefinition.ToDisplayString() == "System.Collections.Generic.KeyValuePair<TKey, TValue>");

    /// <summary>
    /// The element type of an array or <c>IEnumerable&lt;T&gt;</c>-shaped collection, or <c>null</c> for a
    /// non-collection. <c>string</c> is not a collection here, mirroring the runtime's
    /// <c>GetCollectionElementType</c> (an <c>IEnumerable&lt;char&gt;</c> persists as a string).
    /// </summary>
    public static ITypeSymbol? GetCollectionElementType(this ITypeSymbol type)
    {
        if (type.SpecialType == SpecialType.System_String) return null;
        if (type is IArrayTypeSymbol array) return array.ElementType;

        if (type is INamedTypeSymbol named)
        {
            foreach (var candidate in named.AllInterfaces.Concat(new[] { named }))
            {
                if (candidate.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T
                    && candidate.TypeArguments.Length == 1
                    && candidate.TypeArguments[0].SpecialType != SpecialType.System_Char)
                {
                    return candidate.TypeArguments[0];
                }
            }
        }

        return null;
    }

    private static bool HasAttribute(this IPropertySymbol property, string fullName)
        => property.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == fullName);
}
