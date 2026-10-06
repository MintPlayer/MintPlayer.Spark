using Microsoft.CodeAnalysis;
using MintPlayer.Spark.Layering;
using MintPlayer.Spark.SourceGenerators.Diagnostics;
using MintPlayer.Spark.SourceGenerators.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MintPlayer.Spark.SourceGenerators.Json;

/// <summary>
/// The composed model at compile time (composition D6, D14): the referenced libraries' model layers,
/// then the application's <c>Model/*.json</c> AdditionalFiles, through the engine the run time uses
/// (<see cref="SparkModelLayers"/>). So a library type gets its <c>PersistentObjectIds</c> entry in the
/// application, and the security analyzer knows its names, without a copy in the application.
/// </summary>
internal static class ComposedModel
{
    /// <summary>Every <c>Model/*.json</c> the compilation's references ship, in layer order.</summary>
    public static List<LibraryModelFileInfo> LibraryFiles(Compilation compilation)
        => LibraryFiles(LibraryLayersReader.Read(compilation));

    public static List<LibraryModelFileInfo> LibraryFiles(IEnumerable<LibraryLayers> libraries)
        => libraries
            .SelectMany(l => l.Files
                .Where(f => f.Kind == SparkLayerKinds.Model)
                .Select(f => new LibraryModelFileInfo
                {
                    Assembly = l.Assembly,
                    FileName = FileNameOf(f.Path),
                    Json = f.Json,
                    DependsOn = l.DependsOn.ToList(),
                }))
            .ToList();

    /// <summary>
    /// The composed files' text, one per type. An application file the engine cannot read is passed
    /// through as written, for each reader's own lenient fallback; a library type the application
    /// changes an id of keeps the library's id (the run time refuses such a file at startup).
    /// </summary>
    public static IReadOnlyList<string> Compose(IEnumerable<LibraryModelFileInfo> libraries, IEnumerable<AppModelFileInfo> application)
    {
        var composition = SparkModelLayers.Compose(
            libraries.Select(l => new SparkModelInput(l.Assembly, l.FileName, l.Json, isLibrary: true, l.DependsOn)),
            application
                .OrderBy(a => a.Path, StringComparer.Ordinal)
                .Select(a => new SparkModelInput(a.Path, FileNameOf(a.Path), a.Text, isLibrary: false)));
        return composition.Types.Select(t => t.Json).ToList();
    }

    /// <summary>The type names the libraries alone declare, for <c>PersistentObjectNames</c>.</summary>
    public static IReadOnlyList<string> LibraryTypeNames(IEnumerable<LibraryModelFileInfo> libraries)
        => SparkModelLayers.Compose(
                libraries.Select(l => new SparkModelInput(l.Assembly, l.FileName, l.Json, isLibrary: true, l.DependsOn)),
                Array.Empty<SparkModelInput>())
            .Types
            .Select(t => t.Name)
            .OfType<string>()
            .Where(IsIdentifier)
            .ToList();

    private static string FileNameOf(string path)
    {
        var normalized = path.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        return slash < 0 ? normalized : normalized.Substring(slash + 1);
    }

    /// <summary>A C# identifier: the names producer splices the name into source (R2-C5, as <see cref="ModelJsonReader"/>).</summary>
    internal static bool IsIdentifier(string value)
    {
        if (value.Length == 0 || !(value[0] == '_' || char.IsLetter(value[0]))) return false;
        for (var i = 1; i < value.Length; i++)
            if (!(value[i] == '_' || char.IsLetterOrDigit(value[i]))) return false;
        return true;
    }
}

/// <summary>Element-wise equality for a list an incremental step produces, so an unchanged list does not rerun what follows.</summary>
internal sealed class SequenceComparer<T> : IEqualityComparer<List<T>>
{
    public static readonly SequenceComparer<T> Instance = new();

    public bool Equals(List<T>? x, List<T>? y)
        => ReferenceEquals(x, y) || (x is not null && y is not null && x.SequenceEqual(y));

    public int GetHashCode(List<T> obj) => obj.Count;
}
