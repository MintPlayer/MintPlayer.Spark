using System.Reflection;
using MintPlayer.Spark.Layering;

namespace MintPlayer.Spark.Abstractions;

/// <summary>One <c>translations.json</c>: a library's compiled one, or the application's file on disk.</summary>
/// <param name="Name">The layer as messages name it: the assembly name, or <see cref="SparkTranslations.AppLayerName"/>.</param>
/// <param name="Json">The file's text.</param>
/// <param name="IsLibrary">Whether it is a library layer. Only two libraries can conflict, and only a library's <c>""</c> is a value.</param>
/// <param name="DependsOn">The library layers this one stacks above; overriding one of those is intended, never a conflict (grill Q3).</param>
public sealed record SparkTranslationsLayer(string Name, string Json, bool IsLibrary, IReadOnlyList<string>? DependsOn = null);

/// <summary>Two libraries translate the same key into the same language differently. The later library wins.</summary>
public sealed record SparkTranslationsConflict(string Key, string Language, string WinnerLayer, string LoserLayer);

/// <summary>The composed translations: every key with at least one language, and the library conflicts.</summary>
/// <param name="All">Key → text, ordinal; each text's languages in the order they were first stated (the first is the fallback).</param>
public sealed record SparkTranslationsComposition(
    IReadOnlyDictionary<string, TranslatedString> All,
    IReadOnlyList<SparkTranslationsConflict> Conflicts);

/// <summary>
/// Composes <c>translations.json</c> in layers at run time (composition D10): the libraries' compiled
/// layers in layer order (<see cref="SparkLayerCatalog"/>, core first), then the application's file,
/// read from disk. The shared engine does the work (<see cref="SparkKinds.Translations"/>), the same
/// code the analyzers compose with.
/// </summary>
/// <remarks>
/// Each layer is flattened first, so a nested namespace and a dotted key meet. A later layer replaces
/// only the languages it states; <c>"ns": null</c> removes a whole namespace; the application's
/// <c>""</c> means "not translated yet". The running snapshot is held by the host's
/// <c>ITranslationsLoader</c>, which reloads the application's file when it changes.
/// </remarks>
public static class SparkTranslations
{
    /// <summary>The application layer's name in messages.</summary>
    public static string AppLayerName => SparkAppData.Relative("translations.json");

    private static readonly Lazy<IReadOnlyList<SparkTranslationsLayer>> libraries = new(() => From(SparkLayerCatalog.Libraries));

    /// <summary>Every library layer in this process, in layer order (<see cref="SparkLayerCatalog"/>).</summary>
    public static IReadOnlyList<SparkTranslationsLayer> Libraries => libraries.Value;

    /// <summary>The library layers <paramref name="assemblies"/> carry, in layer order.</summary>
    public static IReadOnlyList<SparkTranslationsLayer> Discover(IEnumerable<Assembly> assemblies)
        => From(SparkLayerCatalog.Discover(assemblies));

    /// <summary>The translations layers of <paramref name="libraries"/>, in their order.</summary>
    public static IReadOnlyList<SparkTranslationsLayer> From(IEnumerable<SparkLibrary> libraries)
        => SparkLayerCatalog.Of(libraries, SparkLayerKinds.Translations)
            .Select(x => new SparkTranslationsLayer(x.Library.AssemblyName, x.Layer.Json, IsLibrary: true, x.Library.DependsOn))
            .ToList();

    /// <summary>The application layer's path for a content root.</summary>
    public static string PathFor(string contentRootPath) => SparkAppData.Path(contentRootPath, "translations.json");

    /// <summary>
    /// <paramref name="libraries"/> (<see langword="null"/>: the process's) composed with the
    /// application's file under <paramref name="contentRootPath"/>, when it has one.
    /// </summary>
    /// <exception cref="InvalidOperationException">A layer is not strict JSON or not a valid translations tree.</exception>
    public static SparkTranslationsComposition Compose(string contentRootPath, IReadOnlyList<SparkTranslationsLayer>? libraries = null)
    {
        var path = PathFor(contentRootPath);
        var appJson = File.Exists(path) ? File.ReadAllText(path) : null;
        var layers = libraries ?? Libraries;
        return Compose(appJson is null ? layers : [.. layers, new SparkTranslationsLayer(AppLayerName, appJson, IsLibrary: false)]);
    }

    /// <summary>The layers composed in order.</summary>
    /// <exception cref="InvalidOperationException">A layer is not strict JSON or not a valid translations tree.</exception>
    public static SparkTranslationsComposition Compose(IEnumerable<SparkTranslationsLayer> layers)
    {
        var result = SparkTranslationLayers.Compose(
            layers.Select(l => new SparkTranslationsInput(l.Name, l.Json, l.IsLibrary, l.DependsOn)));

        var all = new Dictionary<string, TranslatedString>(StringComparer.Ordinal);
        foreach (var (key, languages) in result.Entries)
        {
            var translations = new Dictionary<string, string>(languages.Count, StringComparer.Ordinal);
            foreach (var (language, text) in languages)
                translations[language] = text;
            all[key] = new TranslatedString { Translations = translations };
        }

        return new SparkTranslationsComposition(
            all,
            result.Conflicts.Select(c => new SparkTranslationsConflict(c.Key, c.Language, c.WinnerLayer, c.LoserLayer)).ToList());
    }
}
