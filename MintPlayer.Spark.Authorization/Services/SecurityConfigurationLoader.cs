using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Models;

namespace MintPlayer.Spark.Authorization.Services;

[Register(typeof(ISecurityConfigurationLoader), ServiceLifetime.Singleton)]
internal partial class SecurityConfigurationLoader : ISecurityConfigurationLoader, IDisposable
{
    [Inject] private readonly IOptions<AuthorizationOptions> options;
    [Inject] private readonly IHostEnvironment hostEnvironment;
    [Inject] private readonly ILogger<SecurityConfigurationLoader> logger;

    private readonly IMemoryCache cache = new MemoryCache(new MemoryCacheOptions());
    private FileSystemWatcher? fileWatcher;
    private const string CacheKey = "SecurityConfiguration";
    private bool disposed;

    public SecurityConfiguration GetConfiguration()
    {
        if (options.Value.CacheRights && cache.TryGetValue(CacheKey, out SecurityConfiguration? cached) && cached != null)
        {
            return cached;
        }

        var config = LoadFromFile();

        if (options.Value.CacheRights)
        {
            var cacheOptions = new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(TimeSpan.FromMinutes(options.Value.CacheExpirationMinutes));

            cache.Set(CacheKey, config, cacheOptions);

            if (options.Value.EnableHotReload && fileWatcher == null)
            {
                SetupFileWatcher();
            }
        }

        return config;
    }

    public void InvalidateCache()
    {
        cache.Remove(CacheKey);
        logger.LogInformation("Security configuration cache invalidated");
    }

    private SecurityConfiguration LoadFromFile()
    {
        var filePath = Path.Combine(hostEnvironment.ContentRootPath, options.Value.SecurityFilePath);

        if (!File.Exists(filePath))
        {
            logger.LogWarning("Security configuration file not found: {FilePath}. Using empty configuration.", filePath);
            return new SecurityConfiguration();
        }

        try
        {
            var json = File.ReadAllText(filePath);
            var config = JsonSerializer.Deserialize<SecurityConfiguration>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            logger.LogInformation("Loaded security configuration with {GroupCount} groups and {RightCount} rights",
                config?.Groups.Count ?? 0, config?.Rights.Count ?? 0);

            return config ?? new SecurityConfiguration();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load security configuration from {FilePath}", filePath);
            throw;
        }
    }

    private void SetupFileWatcher()
    {
        var filePath = Path.Combine(hostEnvironment.ContentRootPath, options.Value.SecurityFilePath);
        var directory = Path.GetDirectoryName(filePath);
        var fileName = Path.GetFileName(filePath);

        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return;

        fileWatcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
        };

        fileWatcher.Changed += OnFileChanged;
        fileWatcher.EnableRaisingEvents = true;

        logger.LogDebug("File watcher enabled for security configuration: {FilePath}", filePath);
    }

    private void OnFileChanged(object sender, FileSystemEventArgs args)
    {
        // Debounce: file system events can fire multiple times for a single save
        Task.Delay(100).ContinueWith(_ =>
        {
            InvalidateCache();
            logger.LogInformation("Security configuration file changed, cache invalidated");
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
