using MintPlayer.Spark.Layering;

namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Composes <c>moderation.json</c> in layers (composition D3, grill Q6): the defaults a library ships
/// (Moderation's reputation table and privileges), then the application's file, per key ignoring case.
/// Here rather than in the Moderation package because the engine is Abstractions' (D14); the package
/// feeds the result to <c>IConfiguration</c>.
/// </summary>
public static class SparkModerationFiles
{
    /// <summary>
    /// The moderation layers of <paramref name="libraries"/> (<see langword="null"/>: the process's) in
    /// layer order, then <paramref name="appJson"/> when there is one, composed as JSON text.
    /// </summary>
    /// <param name="appName">The application layer as messages name it.</param>
    /// <exception cref="InvalidOperationException">A layer is not strict JSON, or two unrelated libraries disagree.</exception>
    public static string Compose(string? appJson, string appName, IEnumerable<SparkLibrary>? libraries = null)
    {
        var layers = SparkLayerCatalog.Of(libraries ?? SparkLayerCatalog.Libraries, SparkLayerKinds.Moderation)
            .Select(x => SparkLayer.Parse(x.Library.AssemblyName, x.Layer.Json, isLibrary: true))
            .ToList();
        if (appJson is not null)
            layers.Add(SparkLayer.Parse(appName, appJson, isLibrary: false));

        var composition = SparkLayers.Compose(layers, SparkKinds.Moderation);
        if (composition.Errors.Count > 0 || composition.Conflicts.Count > 0)
        {
            var problems = composition.Errors.Select(e => e.ToString())
                .Concat(composition.Conflicts.Select(c => $"{c.WinnerLayer} and {c.LoserLayer} both state '{c.PathText}'"));
            throw new InvalidOperationException("moderation.json does not compose: " + string.Join("; ", problems));
        }
        return SparkJson.Write(composition.Result);
    }
}
