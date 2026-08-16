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

    /// <summary>
    /// Whether <paramref name="property"/> carries <c>[IgnoreProperty]</c> and is therefore not
    /// part of the Spark model. Matched on the fully-qualified attribute name so the check does
    /// not depend on the compilation having a reference symbol available.
    /// </summary>
    public static bool IsIgnoredForSparkModel(this IPropertySymbol property)
        => property.GetAttributes().Any(a =>
            a.AttributeClass?.ToDisplayString() == IgnorePropertyAttributeFullName);
}
