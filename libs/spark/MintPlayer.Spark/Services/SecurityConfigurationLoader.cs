using Microsoft.Extensions.Caching.Memory;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Reads <c>App_Data/security.json</c>, composes it over the rights the referenced libraries ship,
/// validates the result, and derives the expanded rights index the evaluator probes.
/// </summary>
/// <remarks>
/// <para>
/// A library ships default rights (composition D4, decision log row 6), which deliberately reverses
/// the position that rights are the application's alone. The guard rails are what make that safe
/// (<see cref="SparkSecurityFiles"/>): a library only grants, only on what it ships, only to tokens and
/// to slots the application binds. The application removes a grant by key or switches a library off,
/// and the posture table lists every effective right with its layer.
/// </para>
/// <para>
/// Structurally the twin of <see cref="ActionsCatalogueLoader"/> — same cache, same
/// watcher, same fixed path — and deliberately so: both read one JSON file out of
/// <see cref="SparkAppData"/> at startup and reload it when it changes. The file name is not
/// configurable (only its directory is, through <c>SparkAppDataDir</c>) for the
/// same reason that one is not: a second place to put the file is a second place to fail to find
/// it, and the startup gate can only name one location in its message.
/// </para>
/// </remarks>
[Register(typeof(ISecurityConfigurationLoader), ServiceLifetime.Singleton)]
internal partial class SecurityConfigurationLoader : ISecurityConfigurationLoader, IDisposable
{
    [Inject] private readonly IHostEnvironment hostEnvironment;
    [Inject] private readonly ILogger<SecurityConfigurationLoader> logger;

    // Singleton over a singleton that depends only on IHostEnvironment, so no cycle: ModelLoader never
    // asks for authorization.
    [Inject] private readonly IModelLoader modelLoader;

    /// <summary>Where every Spark application's security file lives. See the remarks on the class.</summary>
    public static string FilePath => SparkAppData.Relative(FileName);

    private const string FileName = "security.json";

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
        var filePath = SparkAppData.Path(hostEnvironment.ContentRootPath, FileName);

        if (!File.Exists(filePath))
        {
            throw new SparkSecurityConfigurationException(
                $"""
                Spark requires a security configuration file and none exists at '{filePath}'.
                Authorization is not optional: without this file Spark cannot tell who may reach what, and starting anyway would mean either denying everything or granting everything, both silently.
                Generate a starting point with:  dotnet run -- --spark-init-security
                """);
        }

        SparkSecurityComposition composed;

        try
        {
            // The libraries' rights below, the application's file on top (composition D4). The model's
            // type names keep a library's reserved target from claiming one.
            var types = modelLoader.GetEntityTypes()?.Select(t => t.Name) ?? [];
            composed = SparkSecurityFiles.Compose(File.ReadAllText(filePath), modelTypeNames: types);
        }
        catch (Exception ex) when (ex is not SparkSecurityConfigurationException)
        {
            throw new SparkSecurityConfigurationException(
                $"Spark could not read the security configuration at '{filePath}': {ex.Message}", ex);
        }

        // Every problem at once: a guard rail a library breaks, a token or slot that resolves to no
        // group, an edit of a library grant. Each would otherwise grant to nobody, or to more than the
        // file says, without a word.
        if (composed.Problems.Count > 0)
        {
            throw new SparkSecurityConfigurationException(
                $"The security configuration composed from '{filePath}' and the libraries' rights cannot be trusted:"
                + Environment.NewLine + string.Join(Environment.NewLine, composed.Problems.Select(p => "  - " + p)));
        }

        var loaded = composed.Configuration;

        // Validated on the way out of the loader, so a hot reload is held to the same standard as
        // startup. A file that has drifted into meaninglessness must not quietly replace one that
        // had not.
        // The model is consulted for attribute-level rights ({verb}/{Type}/{Attr}): an unknown type or
        // attribute is refused here, at startup and on every hot reload alike.
        SecurityConfigurationValidator.Validate(loaded, modelLoader);

        logger.LogInformation("Loaded security configuration with {GroupCount} groups and {RightCount} rights ({LibraryRightCount} from libraries, {InertCount} inert)",
            loaded.Groups.Count, loaded.Rights.Count, loaded.Rights.Count(r => r.Layer is not null), loaded.InertRights.Count);

        return loaded;
    }

    public void InvalidateCache()
    {
        cache.Remove(CacheKey);
        logger.LogInformation("Security configuration cache invalidated");
    }

    private void SetupFileWatcher()
    {
        var filePath = SparkAppData.Path(hostEnvironment.ContentRootPath, FileName);
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
