using Microsoft.Extensions.Caching.Memory;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Models;
using System.Text.Json;

namespace MintPlayer.Spark.Services;

public interface ICustomActionsConfigurationLoader
{
    CustomActionsConfiguration GetConfiguration();
    void InvalidateCache();
}

[Register(typeof(ICustomActionsConfigurationLoader), ServiceLifetime.Singleton)]
internal partial class CustomActionsConfigurationLoader : ICustomActionsConfigurationLoader, IDisposable
{
    [Inject] private readonly IHostEnvironment hostEnvironment;
    [Inject] private readonly ILogger<CustomActionsConfigurationLoader> logger;

    private readonly IMemoryCache cache = new MemoryCache(new MemoryCacheOptions());
    private FileSystemWatcher? fileWatcher;
    private const string CacheKey = "CustomActionsConfiguration";
    private const string FilePath = "App_Data/customActions.json";
    private bool disposed;

    public CustomActionsConfiguration GetConfiguration()
    {
        if (cache.TryGetValue(CacheKey, out CustomActionsConfiguration? cached) && cached != null)
        {
            return cached;
        }

        var config = LoadFromFile();

        var cacheOptions = new MemoryCacheEntryOptions()
            .SetAbsoluteExpiration(TimeSpan.FromMinutes(5));

        cache.Set(CacheKey, config, cacheOptions);

        if (fileWatcher == null)
        {
            SetupFileWatcher();
        }

        return config;
    }

    public void InvalidateCache()
    {
        cache.Remove(CacheKey);
        logger.LogInformation("Custom actions configuration cache invalidated");
    }

    private CustomActionsConfiguration LoadFromFile()
    {
        var fullPath = Path.Combine(hostEnvironment.ContentRootPath, FilePath);

        if (!File.Exists(fullPath))
        {
            logger.LogDebug("Custom actions configuration file not found: {FilePath}. Using empty configuration.", fullPath);
            return new CustomActionsConfiguration();
        }

        try
        {
            var json = File.ReadAllText(fullPath);
            var config = JsonSerializer.Deserialize<CustomActionsConfiguration>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            logger.LogInformation("Loaded custom actions configuration with {ActionCount} actions", config?.Count ?? 0);

            ValidateSelectionRules(config, fullPath);

            return config ?? new CustomActionsConfiguration();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load custom actions configuration from {FilePath}", fullPath);
            throw;
        }
    }

    /// <summary>
    /// Rejects a malformed <c>selectionRule</c> when the file is read, naming every offender.
    /// </summary>
    /// <remarks>
    /// Without this the typo survives to the moment somebody presses the button: <c>Parse</c>
    /// throws <see cref="FormatException"/> out of the execute endpoint, so a rule of
    /// <c>"1-5"</c> is a 500 on a user action rather than a refused configuration. The parser has
    /// carried an <c>IsValid</c> for exactly this since it was written, documented as "call at
    /// configuration load", and nothing called it.
    /// <para>
    /// All offenders are reported together. Fixing one typo only to be shown the next is the
    /// worst version of this message.
    /// </para>
    /// </remarks>
    private static void ValidateSelectionRules(CustomActionsConfiguration? config, string fullPath)
    {
        if (config == null) return;

        // The configuration IS the dictionary; the action's name is its key, not a property.
        var invalid = config
            .Where(entry => !SelectionRuleParser.IsValid(entry.Value.SelectionRule))
            .Select(entry => $"'{entry.Key}' declares selectionRule '{entry.Value.SelectionRule}'")
            .ToList();

        if (invalid.Count == 0) return;

        throw new FormatException(
            $"{fullPath} contains {invalid.Count} malformed selection rule(s): {string.Join("; ", invalid)}. "
            + "A rule is a cardinality expression over the number of selected rows, such as '=1', "
            + "'>0', '<=5' or '1<X<5'. Omit the property entirely to require no selection.");
    }

    private void SetupFileWatcher()
    {
        var fullPath = Path.Combine(hostEnvironment.ContentRootPath, FilePath);
        var directory = Path.GetDirectoryName(fullPath);
        var fileName = Path.GetFileName(fullPath);

        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return;

        fileWatcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
        };

        fileWatcher.Changed += OnFileChanged;
        fileWatcher.EnableRaisingEvents = true;

        logger.LogDebug("File watcher enabled for custom actions configuration: {FilePath}", fullPath);
    }

    private void OnFileChanged(object sender, FileSystemEventArgs args)
    {
        Task.Delay(100).ContinueWith(_ =>
        {
            InvalidateCache();
            logger.LogInformation("Custom actions configuration file changed, cache invalidated");
        });
    }

    [NoInterfaceMember]
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        if (fileWatcher != null)
        {
            fileWatcher.Changed -= OnFileChanged;
            fileWatcher.Dispose();
            fileWatcher = null;
        }

        cache.Dispose();
    }
}
