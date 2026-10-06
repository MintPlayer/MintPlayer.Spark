using Microsoft.CodeAnalysis;
using MintPlayer.Spark.Layering;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

/// <summary>A library's compiled <c>actions.json</c>: the assembly and the raw text.</summary>
internal sealed class LibraryActionsLayer(string assembly, string json)
{
    public string Assembly { get; } = assembly;
    public string Json { get; } = json;
}

/// <summary>
/// Reads every <c>[assembly: SparkActions("…")]</c> among the compilation's references (#467, S12),
/// core first and then by assembly name — the order the run time composes them in.
/// </summary>
internal static class LibraryActionsReader
{
    internal const string AttributeMetadataName = "MintPlayer.Spark.Abstractions.SparkActionsAttribute";
    private const string CoreAssemblyName = "MintPlayer.Spark";

    public static IReadOnlyList<LibraryActionsLayer> Read(Compilation compilation)
    {
        var layers = new List<LibraryActionsLayer>();
        var seen = new HashSet<IAssemblySymbol>(SymbolEqualityComparer.Default);
        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            if (!seen.Add(assembly)) continue;
            foreach (var attribute in assembly.GetAttributes())
            {
                if (attribute.AttributeClass?.ToDisplayString() != AttributeMetadataName) continue;
                if (attribute.ConstructorArguments.Length == 1 && attribute.ConstructorArguments[0].Value is string json)
                    layers.Add(new LibraryActionsLayer(assembly.Name, json));
            }
        }

        return layers
            .OrderBy(l => string.Equals(l.Assembly, CoreAssemblyName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(l => l.Assembly, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The action names a layer declares (a removal, <c>"X": null</c>, too: the name is still known),
    /// or nothing when the text does not parse — the run time reports that in its own words.
    /// </summary>
    public static IEnumerable<string> Names(string json)
    {
        SparkJsonNode root;
        try { root = SparkJson.Parse(json); }
        catch (SparkJsonException) { return []; }

        return root is SparkJsonObject actions
            ? actions.Members.Select(m => m.Key).Where(key => !SparkLayers.IsAnnotation(key, isRoot: true)).ToList()
            : [];
    }

    /// <summary>The layers as the shared engine takes them; one that does not parse makes the whole set unreadable.</summary>
    /// <exception cref="SparkLayerException">A layer is not valid JSON, or not an object.</exception>
    public static IEnumerable<SparkLayer> Parse(IEnumerable<LibraryActionsLayer> layers)
        => layers.Select(l => SparkLayer.Parse(l.Assembly, l.Json, isLibrary: true));
}
