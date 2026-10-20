using Microsoft.CodeAnalysis;
using MintPlayer.Spark.Layering;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

/// <summary>One file a referenced library compiled in as <c>[assembly: SparkLayer(…)]</c>.</summary>
/// <param name="json">The text composed: with the application's renames applied (D28).</param>
/// <param name="shippedJson">The text as the library shipped it; <see langword="null"/>: <paramref name="json"/>.</param>
internal sealed class LibraryLayerFile(string kind, string path, string json, string? shippedJson = null)
{
    public string Kind { get; } = kind;
    public string Path { get; } = path;
    public string Json { get; } = json;

    /// <summary>The text as shipped, before renames: what provenance and the declared name come from.</summary>
    public string ShippedJson { get; } = shippedJson ?? json;
}

/// <summary>A referenced assembly that ships layers: its alias, what it stacks above, and its files.</summary>
internal sealed class LibraryLayers(string assembly, string alias, IReadOnlyList<string> dependsOn, IReadOnlyList<LibraryLayerFile> files)
{
    public string Assembly { get; } = assembly;
    public string Alias { get; } = alias;
    public IReadOnlyList<string> DependsOn { get; } = dependsOn;
    public IReadOnlyList<LibraryLayerFile> Files { get; } = files;
}

/// <summary>One <c>[assembly: PersistentObjectName]</c> the compilation states, with where it is written.</summary>
internal sealed class StatedRename(SparkRename rename, AttributeData attribute)
{
    public SparkRename Rename { get; } = rename;
    public AttributeData Attribute { get; } = attribute;
}

/// <summary>
/// Reads every <c>[assembly: SparkLayer(…)]</c> among the compilation's references (composition D1),
/// in the order the run time composes them (<see cref="SparkLibraryOrder"/>), with the application's
/// persistent-object renames applied (D28) exactly as <c>SparkLayerCatalog</c> applies them at startup.
/// </summary>
internal static class LibraryLayersReader
{
    internal const string LayerAttribute = "MintPlayer.Spark.Abstractions.SparkLayerAttribute";
    internal const string DependenciesAttribute = "MintPlayer.Spark.Abstractions.SparkLayerDependenciesAttribute";
    internal const string AssembliesAttribute = "MintPlayer.Spark.Abstractions.SparkLayerAssembliesAttribute";
    internal const string NameAttributeNamespace = "MintPlayer.Spark.Abstractions";
    internal const string NameAttributeName = "PersistentObjectNameAttribute";

    /// <summary>
    /// The referenced libraries' layers; in an application (an executable, like the run time's entry
    /// assembly) with the compilation's own renames applied. A rename that cannot be honoured is left
    /// out here: SPARK061 reports it, and startup refuses it.
    /// </summary>
    public static IReadOnlyList<LibraryLayers> Read(Compilation compilation)
        => Read(compilation, applyRenames: IsApplication(compilation));

    /// <param name="applyRenames">Whether to apply the compilation's <c>[assembly: PersistentObjectName]</c>.</param>
    public static IReadOnlyList<LibraryLayers> Read(Compilation compilation, bool applyRenames)
    {
        var shipped = ReadShipped(compilation);
        if (!applyRenames) return shipped;

        var renames = ReadRenames(compilation);
        return renames.Count == 0 ? shipped : Apply(shipped, Plan(shipped, renames));
    }

    public static bool IsApplication(Compilation compilation)
        => compilation.Options.OutputKind is OutputKind.ConsoleApplication or OutputKind.WindowsApplication;

    /// <summary>The layers as the libraries shipped them.</summary>
    public static IReadOnlyList<LibraryLayers> ReadShipped(Compilation compilation)
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

    /// <summary>The <c>[assembly: PersistentObjectName]</c> / <c>PersistentObjectName&lt;T&gt;</c> attributes the compilation itself states.</summary>
    public static IReadOnlyList<StatedRename> ReadRenames(Compilation compilation)
    {
        var result = new List<StatedRename>();
        foreach (var attribute in compilation.Assembly.GetAttributes())
            if (ToRename(attribute) is { } rename)
                result.Add(new StatedRename(rename, attribute));
        return result;
    }

    /// <summary>Whether <paramref name="type"/> is <c>PersistentObjectNameAttribute</c> or its generic form.</summary>
    public static bool IsNameAttribute(INamedTypeSymbol? type)
        => type is not null
            && type.Name == NameAttributeName
            && type.Arity <= 1
            && type.ContainingNamespace?.ToDisplayString() == NameAttributeNamespace;

    /// <summary>The rename an attribute states, or <see langword="null"/> when it is not one (or does not bind).</summary>
    public static SparkRename? ToRename(AttributeData attribute)
    {
        if (!IsNameAttribute(attribute.AttributeClass)) return null;
        var args = attribute.ConstructorArguments;
        if (attribute.AttributeClass!.Arity == 1)
        {
            if (args.Length != 1 || args[0].Value is not string name) return null;
            if (attribute.AttributeClass.TypeArguments[0] is not INamedTypeSymbol entity) return null;
            return new SparkRename(null, null, MissingReferenceRootAnalyzer.MetadataName(entity), name);
        }

        if (args.Length == 3 && args[0].Value is string library && args[1].Value is string declared && args[2].Value is string renamed)
            return new SparkRename(library, declared, null, renamed);
        return null;
    }

    /// <summary>The renames planned against <paramref name="libraries"/> (as shipped).</summary>
    public static SparkRenamePlan Plan(IReadOnlyList<LibraryLayers> libraries, IEnumerable<StatedRename> renames)
        => SparkRenames.Plan(
            libraries.Select(l => new SparkRenameLibrary(l.Alias, l.Assembly, l.DependsOn,
                l.Files.Where(f => f.Kind == SparkLayerKinds.Model).Select(f => f.ShippedJson).ToList())).ToList(),
            renames.Select(r => r.Rename));

    /// <summary><paramref name="libraries"/> with <paramref name="plan"/> applied; each file keeps its shipped text.</summary>
    public static IReadOnlyList<LibraryLayers> Apply(IReadOnlyList<LibraryLayers> libraries, SparkRenamePlan plan)
    {
        if (plan.IsEmpty) return libraries;
        return libraries
            .Select(library =>
            {
                var map = plan.MapFor(library.Assembly);
                if (map.Count == 0) return library;
                return new LibraryLayers(library.Assembly, library.Alias, library.DependsOn, library.Files
                    .Select(f => new LibraryLayerFile(f.Kind, f.Path, SparkRenames.Rewrite(f.Kind, f.ShippedJson, map), f.ShippedJson))
                    .ToList());
            })
            .ToList();
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
