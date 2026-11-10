namespace MintPlayer.Spark.Services;

/// <summary>
/// A fresh <see cref="IIndexCatalog"/> for the offline paths (#551 D20), which run before any container is built: the
/// implementation the store provider registered (RavenDB's classifies its index types), else the empty catalog.
/// </summary>
internal static class SparkIndexCatalogs
{
    /// <remarks>
    /// Read off the registration's descriptor, the same registration-time trick the host environment and the module
    /// registry use. The last registration wins, as it would in the container. A catalog has no dependencies of its own.
    /// </remarks>
    public static IIndexCatalog CreateOffline(IServiceCollection? services)
    {
        var implementation = services?.LastOrDefault(d => d.ServiceType == typeof(IIndexCatalog))?.ImplementationType;
        return implementation is not null && implementation != typeof(EmptyIndexCatalog)
            ? (IIndexCatalog)Activator.CreateInstance(implementation)!
            : new EmptyIndexCatalog();
    }
}
