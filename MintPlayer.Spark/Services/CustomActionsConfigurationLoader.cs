using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Models;

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

            return config ?? new CustomActionsConfiguration();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load custom actions configuration from {FilePath}", fullPath);
            throw;
        }
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
