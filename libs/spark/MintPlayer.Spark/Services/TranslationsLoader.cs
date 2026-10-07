using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Services;

public interface ITranslationsLoader
{
    TranslatedString? Resolve(string key);

    /// <summary>The composed translations: one snapshot, replaced whole when the application's file changes.</summary>
    IReadOnlyDictionary<string, TranslatedString> GetAll();

    /// <summary>
    /// Raised after a reload replaced the snapshot. The labels other layers resolved against the
    /// translations (the model's, the action catalogue's, the program units', the culture's) follow it
    /// (composition D8).
    /// </summary>
    event Action? Reloaded;
}

/// <summary>
/// The running translations (composition D10): the libraries' compiled <c>translations.json</c> layers,
/// core first, with the application's <c>App_Data/translations.json</c> on top (<see cref="SparkTranslations"/>).
/// The application's file is read from disk and reloaded when it changes; the library layers are fixed
/// at build time.
/// </summary>
/// <remarks>
/// Reloaded through the one watcher policy (<see cref="AppLayerSnapshot{T}"/>, D8). A file that does not
/// compose on reload is logged and the previous snapshot stays, so a half-saved edit does not take the
/// running application's texts away.
/// </remarks>
[Register(typeof(ITranslationsLoader), ServiceLifetime.Singleton)]
internal partial class TranslationsLoader : ITranslationsLoader, IDisposable
{
    [Inject] private readonly IHostEnvironment hostEnvironment;
    [Inject] private readonly ILogger<TranslationsLoader> logger;

    private AppLayerSnapshot<IReadOnlyDictionary<string, TranslatedString>>? layer;

    /// <summary>The libraries to compose; <see langword="null"/>: the process's (<see cref="SparkLayerCatalog"/>).</summary>
    private IReadOnlyList<SparkTranslationsLayer>? libraries;

    /// <summary>
    /// A loader over chosen library layers (<c>[]</c>: the application's file alone), for a test that
    /// must not depend on which layered libraries its process happens to reference.
    /// </summary>
    internal static TranslationsLoader For(IHostEnvironment hostEnvironment, IReadOnlyList<SparkTranslationsLayer>? libraries = null, ILogger<TranslationsLoader>? logger = null)
        => new(hostEnvironment, logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TranslationsLoader>.Instance) { libraries = libraries };

    private AppLayerSnapshot<IReadOnlyDictionary<string, TranslatedString>> Layer
        => LazyInitializer.EnsureInitialized(ref layer, () => new(
            SparkTranslations.AppLayerName,
            Path.GetDirectoryName(SparkTranslations.PathFor(hostEnvironment.ContentRootPath)),
            ["translations.json"],
            Compose,
            logger));

    public event Action? Reloaded
    {
        add => Layer.Reloaded += value;
        remove => Layer.Reloaded -= value;
    }

    public TranslatedString? Resolve(string key)
        => GetAll().TryGetValue(key, out var ts) ? ts : null;

    public IReadOnlyDictionary<string, TranslatedString> GetAll() => Layer.Current;

    /// <summary>Composes the layers again and swaps the snapshot; keeps the previous one when they do not compose.</summary>
    /// <returns>Whether the snapshot was replaced.</returns>
    internal bool Reload() => Layer.Reload();

    private IReadOnlyDictionary<string, TranslatedString> Compose()
    {
        var composition = SparkTranslations.Compose(hostEnvironment.ContentRootPath, libraries);
        foreach (var conflict in composition.Conflicts)
        {
            logger.LogWarning(
                "Libraries '{Winner}' and '{Loser}' both translate '{Key}' into '{Language}', with different values (SPARK_TRANS_005). "
                + "'{Winner}' wins (libraries apply in dependency order). Define it in {TranslationsFile} to choose.",
                conflict.WinnerLayer, conflict.LoserLayer, conflict.Key, conflict.Language, conflict.WinnerLayer, SparkTranslations.AppLayerName);
        }
        logger.LogInformation("Composed the translations: {KeyCount} keys", composition.All.Count);
        return composition.All;
    }

    [NoInterfaceMember]
    public void Dispose() => layer?.Dispose();
}
