using Microsoft.CodeAnalysis;
using MintPlayer.Spark.Layering;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

/// <summary>One file a referenced library compiled in as <c>[assembly: SparkLayer(…)]</c>.</summary>
internal sealed class LibraryLayerFile(string kind, string path, string json)
{
    public string Kind { get; } = kind;
    public string Path { get; } = path;
    public string Json { get; } = json;
}

/// <summary>A referenced assembly that ships layers: its alias, what it stacks above, and its files.</summary>
internal sealed class LibraryLayers(string assembly, string alias, IReadOnlyList<string> dependsOn, IReadOnlyList<LibraryLayerFile> files)
{
    public string Assembly { get; } = assembly;
    public string Alias { get; } = alias;
    public IReadOnlyList<string> DependsOn { get; } = dependsOn;
    public IReadOnlyList<LibraryLayerFile> Files { get; } = files;
}

/// <summary>
/// Reads every <c>[assembly: SparkLayer(…)]</c> among the compilation's references (composition D1),
/// in the order the run time composes them (<see cref="SparkLibraryOrder"/>).
/// </summary>
internal static class LibraryLayersReader
{
    internal const string LayerAttribute = "MintPlayer.Spark.Abstractions.SparkLayerAttribute";
    internal const string DependenciesAttribute = "MintPlayer.Spark.Abstractions.SparkLayerDependenciesAttribute";
    internal const string AssembliesAttribute = "MintPlayer.Spark.Abstractions.SparkLayerAssembliesAttribute";

    public static IReadOnlyList<LibraryLayers> Read(Compilation compilation)
    {
        var libraries = new List<LibraryLayers>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            if (!seen.Add(assembly.Name)) continue;

            string? alias = null;
            var files = new List<LibraryLayerFile>();
            var dependsOn = new List<string>();
            foreach (var attribute in assembly.GetAttributes())
            {
                var name = attribute.AttributeClass?.ToDisplayString();
                var args = attribute.ConstructorArguments;
                if (name == LayerAttribute
                    && args.Length == 4
                    && args[0].Value is string a && args[1].Value is string kind && args[2].Value is string path && args[3].Value is string json)
                {
                    alias ??= a;
                    files.Add(new LibraryLayerFile(kind, path, json));
                }
                else if (name == DependenciesAttribute && args.Length == 1 && args[0].Kind == TypedConstantKind.Array)
                {
                    dependsOn.AddRange(args[0].Values.Select(v => v.Value).OfType<string>());
                }
            }

            if (alias is not null)
                libraries.Add(new LibraryLayers(assembly.Name, alias, dependsOn, files));
        }

        return SparkLibraryOrder.Sort(libraries, l => l.Assembly, l => l.DependsOn);
    }

    /// <summary>The layers of one kind, as the shared engine takes them, named by assembly.</summary>
    /// <exception cref="SparkLayerException">A layer is not valid JSON, or not an object.</exception>
    public static IEnumerable<SparkLayer> Parse(IEnumerable<LibraryLayers> libraries, string kind)
        => libraries.SelectMany(l => l.Files
            .Where(f => f.Kind == kind)
            .Select(f => SparkLayer.Parse(l.Assembly, f.Json, isLibrary: true)));

    /// <summary>
    /// The action names a layer declares (a removal, <c>"X": null</c>, too: the name is still known),
    /// or nothing when the text does not parse — the run time reports that in its own words.
    /// </summary>
    public static IEnumerable<string> ActionNames(string json)
    {
        SparkJsonNode root;
        try { root = SparkJson.Parse(json); }
        catch (SparkJsonException) { return []; }

        return root is SparkJsonObject actions
            ? actions.Members.Select(m => m.Key).Where(key => !SparkLayers.IsAnnotation(key, isRoot: true)).ToList()
            : [];
    }
}
