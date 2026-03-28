using Microsoft.AspNetCore.Builder;

namespace MintPlayer.Spark.Storage;

/// <summary>
/// Provider interface for storage-backend-specific operations that cannot be
/// expressed purely through <see cref="ISparkSession"/> and <see cref="IQueryable{T}"/>.
/// Each storage backend (RavenDB, FileSystem, etc.) implements this interface.
/// </summary>
public interface ISparkStorageProvider
{
    /// <summary>
    /// Called during UseSpark() to perform provider-specific initialization
    /// (e.g., create indexes, ensure database exists, scan assemblies).
    /// </summary>
    void Initialize(IApplicationBuilder app);

    /// <summary>
    /// Materializes an <see cref="IQueryable{T}"/> to a list asynchronously.
    /// Each provider uses its own async materialization strategy
    /// (e.g., RavenDB's LinqExtensions.ToListAsync, EF Core's ToListAsync, or in-memory ToList).
    /// </summary>
    Task<List<T>> ToListAsync<T>(IQueryable<T> queryable, CancellationToken ct = default);

    /// <summary>
    /// Applies eager-loading of referenced documents (e.g., RavenDB .Include()).
    /// Returns the (possibly wrapped) queryable.
    /// </summary>
    /// <param name="queryable">The queryable to apply includes to.</param>
    /// <param name="propertyPaths">Property paths to include.</param>
    /// <returns>The queryable with includes applied.</returns>
    object ApplyIncludes(object queryable, IEnumerable<string> propertyPaths);

    /// <summary>
    /// Applies provider-specific projection to a queryable (e.g., RavenDB ProjectInto).
    /// </summary>
    /// <param name="queryable">The queryable to project.</param>
    /// <param name="resultType">The target projection type.</param>
    /// <returns>The projected queryable.</returns>
    object ApplyProjection(object queryable, Type resultType);

    /// <summary>
    /// Resolves the collection name for a CLR type.
    /// </summary>
    string? GetCollectionName(Type clrType);
}
