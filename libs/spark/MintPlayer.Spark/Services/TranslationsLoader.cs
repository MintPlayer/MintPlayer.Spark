using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Services;

public interface ITranslationsLoader
{
    TranslatedString? Resolve(string key);

    /// <summary>The composed translations: one snapshot, replaced whole when the application's file changes.</summary>
    IReadOnlyDictionary<string, TranslatedString> GetAll();
}

/// <summary>
/// The running translations (composition D10): the libraries' compiled <c>translations.json</c> layers,
/// core first, with the application's <c>App_Data/translations.json</c> on top (<see cref="SparkTranslations"/>).
/// The application's file is read from disk and reloaded when it changes; the library layers are fixed
/// at build time.
/// </summary>
/// <remarks>
/// The first read composes and throws when a layer cannot be read, so a broken file stops startup. A
/// reload swaps the snapshot atomically: a reader sees the old one or the new one, never a mix. A file
/// that does not compose on reload is logged and the previous snapshot stays, so a half-saved edit does
/// not take the running application's texts away.
/// </remarks>
[Register(typeof(ITranslationsLoader), ServiceLifetime.Singleton)]
internal partial class TranslationsLoader : ITranslationsLoader, IDisposable
{
    [Inject] private readonly IHostEnvironment hostEnvironment;
    [Inject] private readonly ILogger<TranslationsLoader> logger;

    private readonly object gate = new();
    private volatile IReadOnlyDictionary<string, TranslatedString>? snapshot;
    private FileSystemWatcher? fileWatcher;
    private bool disposed;

    /// <summary>The libraries to compose; <see langword="null"/>: the process's (<see cref="SparkLayerCatalog"/>).</summary>
    private IReadOnlyList<SparkTranslationsLayer>? libraries;

    /// <summary>
    /// A loader over chosen library layers (<c>[]</c>: the application's file alone), for a test that
    /// must not depend on which layered libraries its process happens to reference.
    /// </summary>
    internal static TranslationsLoader For(IHostEnvironment hostEnvironment, IReadOnlyList<SparkTranslationsLayer>? libraries = null, ILogger<TranslationsLoader>? logger = null)
        => new(hostEnvironment, logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TranslationsLoader>.Instance) { libraries = libraries };

    public TranslatedString? Resolve(string key)
        => GetAll().TryGetValue(key, out var ts) ? ts : null;

    public IReadOnlyDictionary<string, TranslatedString> GetAll()
    {
        if (snapshot is { } current) return current;

        lock (gate)
        {
            if (snapshot is null)
            {
                snapshot = Compose();
                SetupFileWatcher();
            }
            return snapshot;
        }
    }

    /// <summary>Composes the layers again and swaps the snapshot; keeps the previous one when they do not compose.</summary>
    /// <returns>Whether the snapshot was replaced.</returns>
    internal bool Reload()
    {
        lock (gate)
        {
            try
            {
                snapshot = Compose();
                return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                logger.LogError(ex, "Failed to reload {TranslationsFile}; the previous translations stay in use", SparkTranslations.AppLayerName);
                return false;
            }
        }
    }

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

    private void SetupFileWatcher()
    {
        var directory = Path.GetDirectoryName(SparkTranslations.PathFor(hostEnvironment.ContentRootPath));
        if (disposed || fileWatcher is not null || string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return;

        fileWatcher = new FileSystemWatcher(directory, "translations.json")
        {
            // Created and Renamed too: an editor that saves through a temporary file replaces the
            // file rather than writing it, and an application without one may add it while running.
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };

        fileWatcher.Changed += OnFileChanged;
        fileWatcher.Created += OnFileChanged;
        fileWatcher.Deleted += OnFileChanged;
        fileWatcher.Renamed += OnFileChanged;
        fileWatcher.EnableRaisingEvents = true;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs args)
    {
        Task.Delay(100).ContinueWith(_ => { if (!disposed) Reload(); });
    }

    [NoInterfaceMember]
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        if (fileWatcher != null)
        {
            fileWatcher.Changed -= OnFileChanged;
            fileWatcher.Created -= OnFileChanged;
            fileWatcher.Deleted -= OnFileChanged;
            fileWatcher.Renamed -= OnFileChanged;
            fileWatcher.Dispose();
            fileWatcher = null;
        }
    }
}
