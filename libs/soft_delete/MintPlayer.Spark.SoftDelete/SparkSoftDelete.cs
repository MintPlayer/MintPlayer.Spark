using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.SoftDelete;

/// <inheritdoc />
internal sealed partial class SparkSoftDelete : ISparkSoftDelete
{
    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly SoftDeleteRequestState state;

    public async Task DeleteAsync(Guid objectTypeId, string id, string? reason = null, CancellationToken cancellationToken = default)
    {
        RequireSoftDeletable(objectTypeId);

        // The same order as POST /spark/po/delete: read through the row gate first, so a row the
        // caller cannot see is refused like one that does not exist.
        if (await databaseAccess.GetPersistentObjectAsync(objectTypeId, id) is null)
            throw new SparkRowLevelAccessDeniedException($"Delete/{objectTypeId}");

        state.PendingReason = reason;
        try
        {
            await databaseAccess.DeletePersistentObjectAsync(objectTypeId, id);
        }
        finally
        {
            state.PendingReason = null;
        }
    }

    public async Task<string?> RestoreAsync(Guid objectTypeId, string id, CancellationToken cancellationToken = default)
    {
        var definition = RequireSoftDeletable(objectTypeId);

        // No attributes: the base save merges only what is posted, so every stored field survives and
        // the interceptor clears the four soft-delete fields. Gated by DatabaseAccess under "Restore".
        var po = new PersistentObject { Id = id, ObjectTypeId = objectTypeId, Name = definition.Name };
        await databaseAccess.SavePersistentObjectAsync(po, PersistentObjectOperation.Restore);
        return po.Etag;
    }

    public async Task PurgeAsync(Guid objectTypeId, string id, CancellationToken cancellationToken = default)
    {
        RequireSoftDeletable(objectTypeId);

        await databaseAccess.DeletePersistentObjectAsync(objectTypeId, id, PersistentObjectOperation.Purge);

        // DatabaseAccess returns quietly for a missing or foreign-collection id; only the interceptor's
        // after-hook knows a purge really happened.
        if (!state.Purged.Contains(id))
            throw new SparkRowLevelAccessDeniedException($"Purge/{objectTypeId}");
    }

    /// <summary>A type that is not soft-deletable has nothing to restore or purge: refused like a missing row.</summary>
    private EntityTypeDefinition RequireSoftDeletable(Guid objectTypeId)
    {
        var definition = modelLoader.GetEntityType(objectTypeId);
        var clrType = SoftDeleteTypes.Resolve(definition?.ClrType);
        if (definition is null || clrType is null || !typeof(ISoftDeletable).IsAssignableFrom(clrType))
            throw new SparkRowLevelAccessDeniedException($"SoftDelete/{objectTypeId}");
        return definition;
    }
}

/// <summary>Model CLR-type name to <see cref="Type"/>, the way core resolves it.</summary>
internal static class SoftDeleteTypes
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Type?> Cache = new();

    public static Type? Resolve(string? clrType) => clrType is null ? null : Cache.GetOrAdd(clrType, static name =>
    {
        if (Type.GetType(name) is { } type)
            return type;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            if (assembly.GetType(name) is { } found)
                return found;
        return null;
    });
}
