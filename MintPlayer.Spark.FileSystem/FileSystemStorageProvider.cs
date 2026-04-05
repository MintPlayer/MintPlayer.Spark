using Microsoft.AspNetCore.Builder;
using MintPlayer.Spark.Storage;

namespace MintPlayer.Spark.FileSystem;

/// <summary>
/// File-based implementation of <see cref="ISparkStorageProvider"/>.
/// No indexes, no projections — queries are in-memory LINQ-to-Objects.
/// </summary>
public class FileSystemStorageProvider : ISparkStorageProvider
{
    public void Initialize(IApplicationBuilder app)
    {
        // No indexes to create for file-based storage
    }

    public Task<List<T>> ToListAsync<T>(IQueryable<T> queryable, CancellationToken ct = default)
    {
        // In-memory queryable — just materialize synchronously
        return Task.FromResult(queryable.ToList());
    }

    public object ApplyIncludes(object queryable, IEnumerable<string> propertyPaths)
    {
        // No-op for file storage — includes are not needed (all data loaded from files)
        return queryable;
    }

    public object ApplyProjection(object queryable, Type resultType)
    {
        // No-op for file storage — no index projections
        return queryable;
    }

    public string? GetCollectionName(Type clrType)
    {
        return clrType.Name + "s";
    }
}
