using Microsoft.CodeAnalysis;
using MintPlayer.Spark.SourceGenerators.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

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
        => Entries(json).Select(e => e.Key);

    /// <summary>The layer's actions, each with its properties rendered canonically; a removal has none.</summary>
    public static IReadOnlyList<KeyValuePair<string, IReadOnlyList<KeyValuePair<string, string>>?>> Entries(string json)
    {
        var result = new List<KeyValuePair<string, IReadOnlyList<KeyValuePair<string, string>>?>>();
        JsonNode root;
        try { root = MiniJson.Parse(json, allowScalars: true); }
        catch (JsonParseException) { return result; }

        if (root is not JsonObject actions) return result;
        foreach (var action in actions.Members)
        {
            // $schema and _comments are not actions or properties; SparkActionLayers skips them too.
            if (action.Key.StartsWith("_", StringComparison.Ordinal) || action.Key == "$schema")
                continue;
            if (action.Value is JsonObject definition)
            {
                var properties = definition.Members
                    .Where(p => !p.Key.StartsWith("_", StringComparison.Ordinal))
                    .Select(p => new KeyValuePair<string, string>(p.Key, Render(p.Value)))
                    .ToList();
                result.Add(new KeyValuePair<string, IReadOnlyList<KeyValuePair<string, string>>?>(action.Key, properties));
            }
            else
            {
                result.Add(new KeyValuePair<string, IReadOnlyList<KeyValuePair<string, string>>?>(action.Key, null));
            }
        }
        return result;
    }

    private static string Render(JsonNode node)
    {
        switch (node)
        {
            case JsonString s:
                var sb = new StringBuilder();
                MiniJson.AppendString(sb, s.Value);
                return sb.ToString();
            case JsonScalar scalar:
                return scalar.Raw;
            case JsonObject o:
                return "{" + string.Join(",", o.Members
                    .OrderBy(m => m.Key, StringComparer.Ordinal)
                    .Select(m => m.Key + ":" + Render(m.Value))) + "}";
            default:
                return "null";
        }
    }
}
