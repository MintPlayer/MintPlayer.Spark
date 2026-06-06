namespace MintPlayer.Spark.Authorization.Configuration;

/// <summary>
/// Configuration options for Spark authorization.
/// </summary>
public class AuthorizationOptions
{
    /// <summary>
    /// Path to the security configuration file relative to ContentRootPath.
    /// Default: "App_Data/security.json"
    /// </summary>
    public string SecurityFilePath { get; set; } = "App_Data/security.json";

    /// <summary>
    /// Default behavior when no permission is explicitly defined for a resource.
    /// </summary>
    public DefaultAccessBehavior DefaultBehavior { get; set; } = DefaultAccessBehavior.DenyAll;

    /// <summary>
    /// Whether to cache rights in memory for improved performance.
    /// </summary>
    public bool CacheRights { get; set; } = true;

    /// <summary>
    /// Cache expiration in minutes. Only used when CacheRights is true.
    /// </summary>
    public int CacheExpirationMinutes { get; set; } = 5;

    /// <summary>
    /// Enable file watcher for hot-reload of security.json changes.
    /// When enabled, changes to security.json will automatically invalidate the cache.
    /// </summary>
    public bool EnableHotReload { get; set; } = true;
}

/// <summary>
/// Determines the default access behavior when no explicit permission is found.
/// </summary>
public enum DefaultAccessBehavior
{
    /// <summary>
    /// Deny access if no matching permission is found (recommended for production).
    /// </summary>
    DenyAll,

    /// <summary>
    /// Allow access if no matching permission is found (useful for development).
    /// </summary>
    AllowAll
}
