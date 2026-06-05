using MintPlayer.Spark.Authorization.Models;

namespace MintPlayer.Spark.Authorization.Services;

/// <summary>
/// Interface for loading and caching security configuration.
/// </summary>
public interface ISecurityConfigurationLoader
{
    /// <summary>
    /// Gets the current security configuration.
    /// May return a cached version if caching is enabled.
    /// </summary>
    SecurityConfiguration GetConfiguration();

    /// <summary>
    /// Invalidates the cached configuration, forcing a reload on next access.
    /// </summary>
    void InvalidateCache();
}
