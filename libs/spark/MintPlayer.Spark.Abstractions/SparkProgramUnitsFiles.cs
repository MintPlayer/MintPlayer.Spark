using MintPlayer.Spark.Layering;

namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Composes <c>programUnits.json</c> in layers (composition D3): the menu a library ships, then the
/// application's file, groups and units merged by <c>id</c> (<see cref="SparkKinds.ProgramUnits"/>).
/// One composer for the loader, the hash gate and <c>--spark-describe</c>, so none of them reads a
/// different menu than the others.
/// </summary>
public static class SparkProgramUnitsFiles
{
    /// <summary>The application layer's name in messages.</summary>
    public static string AppLayerName => SparkAppData.Relative(FileName);

    internal const string FileName = "programUnits.json";

    /// <summary>The application layer's path for a content root.</summary>
    public static string PathFor(string contentRootPath) => SparkAppData.Path(contentRootPath, FileName);

    /// <summary>
    /// The program-unit layers of <paramref name="libraries"/> (<see langword="null"/>: the process's), then
    /// <paramref name="appJson"/> when there is one, as JSON text; <see langword="null"/> when no layer exists,
    /// which is an application without a menu.
    /// </summary>
    /// <exception cref="InvalidOperationException">A layer is not strict JSON, or two unrelated libraries disagree.</exception>
    public static string? Compose(string? appJson, IEnumerable<SparkLibrary>? libraries = null)
        => ComposeLayers(appJson, libraries) is { } composition ? SparkJson.Write(composition.Result) : null;

    /// <summary>As <see cref="Compose"/>, with the provenance of every leaf (for <c>--spark-describe</c>).</summary>
    internal static SparkComposition? ComposeLayers(string? appJson, IEnumerable<SparkLibrary>? libraries = null)
    {
        var layers = SparkLayerCatalog.Of(libraries ?? SparkLayerCatalog.Libraries, SparkLayerKinds.ProgramUnits)
            .Select(x => (Layer: SparkLayer.Parse(x.Library.AssemblyName, x.Layer.Json, isLibrary: true), x.Library.DependsOn))
            .ToList();
        if (appJson is not null)
            layers.Add((SparkLayer.Parse(AppLayerName, appJson, isLibrary: false), []));
        if (layers.Count == 0)
            return null;

        var composition = SparkLayers.Compose(layers.Select(l => l.Layer), SparkKinds.ProgramUnits);

        // A library overriding one it depends on means it (grill Q3); only unrelated ones conflict.
        var dependsOn = layers.ToDictionary(l => l.Layer.Name, l => l.DependsOn, StringComparer.Ordinal);
        var conflicts = composition.Conflicts
            .Where(c => !(dependsOn.TryGetValue(c.WinnerLayer, out var below) && below.Contains(c.LoserLayer, StringComparer.Ordinal)))
            .ToList();
        if (composition.Errors.Count > 0 || conflicts.Count > 0)
        {
            var problems = composition.Errors.Select(e => e.ToString())
                .Concat(conflicts.Select(c => $"{c.WinnerLayer} and {c.LoserLayer} both state '{c.PathText}'"));
            throw new InvalidOperationException($"{FileName} does not compose: " + string.Join("; ", problems));
        }
        return composition;
    }
}
