using Microsoft.Extensions.Caching.Memory;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using System.Text.Json;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Reads <c>App_Data/security.json</c>, validates it, and derives the expanded rights index the
/// evaluator probes.
/// </summary>
/// <remarks>
/// Structurally the twin of <see cref="CustomActionsConfigurationLoader"/> — same cache, same
/// watcher, same fixed path — and deliberately so: both read one JSON file out of
/// <c>App_Data</c> at startup and reload it when it changes. The path is not configurable for the
/// same reason that one is not: a second place to put the file is a second place to fail to find
/// it, and the startup gate can only name one location in its message.
/// </remarks>
[Register(typeof(ISecurityConfigurationLoader), ServiceLifetime.Singleton)]
internal partial class SecurityConfigurationLoader : ISecurityConfigurationLoader, IDisposable
{
    [Inject] private readonly IHostEnvironment hostEnvironment;
    [Inject] private readonly ILogger<SecurityConfigurationLoader> logger;

    /// <summary>Where every Spark application's security file lives. See the remarks on the class.</summary>
    public const string FilePath = "App_Data/security.json";

    private readonly IMemoryCache cache = new MemoryCache(new MemoryCacheOptions());
    private FileSystemWatcher? fileWatcher;
    private const string CacheKey = "SecurityConfiguration";
    private bool disposed;

    /// <summary>
    /// The expanded per-group index, keyed by the configuration it was derived from.
    /// <para>
    /// Keyed by instance rather than cleared on reload, so a request holding the previous
    /// configuration keeps reading the index that matches it. A hot reload swaps the configuration
    /// out from under in-flight requests; pairing the two by identity means a request can never
    /// evaluate one file's rights against another file's expansion.
    /// </para>
    /// </summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<SecurityConfiguration, IReadOnlyDictionary<Guid, GroupRights>> expanded = new();

    public SecurityConfiguration GetConfiguration()
    {
        if (cache.TryGetValue(CacheKey, out SecurityConfiguration? cached) && cached != null)
            return cached;

        var config = LoadFromFile();

        cache.Set(CacheKey, config, new MemoryCacheEntryOptions()
            .SetAbsoluteExpiration(TimeSpan.FromMinutes(5)));

        if (fileWatcher == null)
            SetupFileWatcher();

        return config;
    }

    public RightsDecision GetResolvedRights(IReadOnlySet<Guid> groupIds)
    {
        if (groupIds.Count == 0)
            return RightsDecision.None;

        var config = GetConfiguration();
        return RightsDecision.Over(expanded.GetValue(config, GroupRights.Index), groupIds);
    }

    private SecurityConfiguration LoadFromFile()
    {
        var filePath = Path.Combine(hostEnvironment.ContentRootPath, FilePath);

        if (!File.Exists(filePath))
        {
            throw new SparkSecurityConfigurationException(
                $"Spark requires a security configuration file and none exists at '{filePath}'.\n"
                + "Authorization is not optional: without this file Spark cannot tell who may reach "
                + "what, and starting anyway would mean either denying everything or granting "
                + "everything, both silently.\n"
                + "Generate a starting point with:  dotnet run -- --spark-init-security");
        }

        SecurityConfiguration loaded;

        try
        {
            var json = File.ReadAllText(filePath);
            loaded = JsonSerializer.Deserialize<SecurityConfiguration>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new SecurityConfiguration();
        }
        catch (Exception ex) when (ex is not SparkSecurityConfigurationException)
        {
            throw new SparkSecurityConfigurationException(
                $"Spark could not read the security configuration at '{filePath}': {ex.Message}", ex);
        }

        // Validated on the way out of the loader, so a hot reload is held to the same standard as
        // startup. A file that has drifted into meaninglessness must not quietly replace one that
        // had not.
        SecurityConfigurationValidator.Validate(loaded);

        logger.LogInformation("Loaded security configuration with {GroupCount} groups and {RightCount} rights",
            loaded.Groups.Count, loaded.Rights.Count);

        return loaded;
    }

    public void InvalidateCache()
    {
        cache.Remove(CacheKey);
        logger.LogInformation("Security configuration cache invalidated");
    }

    private void SetupFileWatcher()
    {
        var filePath = Path.Combine(hostEnvironment.ContentRootPath, FilePath);
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

/// <summary>
/// Thrown when <c>security.json</c> is missing, unreadable, or means something other than it looks
/// like. Distinct from a bare <see cref="InvalidOperationException"/> so the startup gate can
/// present it as a configuration problem rather than as a crash.
/// </summary>
public sealed class SparkSecurityConfigurationException : Exception
{
    public SparkSecurityConfigurationException(string message) : base(message) { }
    public SparkSecurityConfigurationException(string message, Exception inner) : base(message, inner) { }
}
