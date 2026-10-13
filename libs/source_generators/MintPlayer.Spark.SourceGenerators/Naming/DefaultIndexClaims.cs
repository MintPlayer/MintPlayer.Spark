using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using System.Linq;

namespace MintPlayer.Spark.SourceGenerators.Naming;

/// <summary>
/// What a <c>[DefaultIndex(params Type[] entityTypes)]</c> marker claims (<c>docs/default_index_PRD.md</c>, D1),
/// read the one way the generator (D5) and <c>DefaultIndexAnalyzer</c> (SPARK009, SPARK051–055) both need.
/// </summary>
/// <remarks>
/// Mirrors the runtime's <c>IndexCatalog.ResolveClaims</c>: a single-map claims its mapped <c>T</c> (an explicit
/// argument may only repeat it), a multi-map claims exactly the types it lists, and a JavaScript or otherwise
/// opaque index claims nothing (the catalog refuses a marker there at startup).
/// </remarks>
internal static class DefaultIndexClaims
{
    internal const string DefaultIndexAttributeFullName = "MintPlayer.Spark.Abstractions.DefaultIndexAttribute";

    private const string AbstractJavaScriptIndexCreationTaskName = "AbstractJavaScriptIndexCreationTask";

    /// <summary>The <c>[DefaultIndex]</c> attribute on <paramref name="type"/>, or <see langword="null"/>.</summary>
    internal static AttributeData? Marker(INamedTypeSymbol type)
        => type.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == DefaultIndexAttributeFullName);

    /// <summary>
    /// The types the marker lists, in source order. Empty for a bare marker.
    /// </summary>
    /// <remarks>
    /// ⚠️ A bare <c>[DefaultIndex]</c> against the <c>params</c> constructor arrives as <b>one</b> constructor
    /// argument holding an <b>empty array</b>, not as zero arguments (PRD §7, SP2). Counting arguments would read
    /// every bare marker as "lists types". An unbound <c>typeof(Missing)</c> yields a non-named symbol and is
    /// skipped: the compiler already reports it.
    /// </remarks>
    internal static List<INamedTypeSymbol> ExplicitTypes(AttributeData marker)
    {
        var types = new List<INamedTypeSymbol>();
        foreach (var argument in marker.ConstructorArguments)
        {
            if (argument.Kind == TypedConstantKind.Array)
            {
                if (argument.IsNull) continue;
                types.AddRange(argument.Values.Select(v => v.Value).OfType<INamedTypeSymbol>());
            }
            else if (argument.Value is INamedTypeSymbol single)
            {
                types.Add(single);
            }
        }

        return types;
    }

    /// <summary>
    /// The entity types <paramref name="index"/> claims through <paramref name="marker"/>: the listed types of a
    /// multi-map, the mapped <c>T</c> of a single-map (whatever it lists; SPARK051 reports a wrong argument), and
    /// nothing for an abstract, JavaScript or opaque index.
    /// </summary>
    internal static List<INamedTypeSymbol> ClaimedTypes(INamedTypeSymbol index, AttributeData marker)
    {
        if (index.IsAbstract) return new List<INamedTypeSymbol>();

        switch (RavenIndexHierarchy.Classify(index, out var mapped))
        {
            case RavenIndexKind.Map when mapped is not null:
                return new List<INamedTypeSymbol> { mapped };
            case RavenIndexKind.MultiMap:
                return ExplicitTypes(marker)
                    .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default)
                    .ToList();
            default:
                return new List<INamedTypeSymbol>();
        }
    }

    /// <summary>Whether <paramref name="index"/> is a RavenDB JavaScript index (<c>AbstractJavaScriptIndexCreationTask</c>).</summary>
    internal static bool IsJavaScript(INamedTypeSymbol index)
    {
        for (var current = index.BaseType; current is not null; current = current.BaseType)
        {
            if (current.Name == AbstractJavaScriptIndexCreationTaskName
                && current.ContainingNamespace?.ToDisplayString() == RavenIndexHierarchy.RavenIndexesNamespace)
                return true;
        }

        return false;
    }
}
