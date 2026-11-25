using System.Collections.Concurrent;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Moderation.Services;

/// <summary>A moderatable row the caller can see.</summary>
internal sealed record ModerationTarget(string Id, EntityTypeDefinition Definition, Type ClrType, IModeratable Entity)
{
    public string TypeName => Definition.Name;
    public string? AuthorId => Entity.AuthorId;
    public DateTime? PostedAtUtc => Entity.PostedAt?.UtcDateTime;
}

/// <summary>
/// Resolves a vote/flag/lock target <b>through the row gate</b>: a row the caller cannot see, a type
/// that is not <see cref="IModeratable"/>, and an id that names nothing are the same refusal (#453:
/// missing ≡ no access).
/// </summary>
internal sealed partial class ModerationTargets
{
    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly ISparkSession session;

    private static readonly ConcurrentDictionary<string, Type?> Types = new();

    /// <summary>The target, or a <see cref="SparkRowLevelAccessDeniedException"/> (answered 401/404).</summary>
    public async Task<ModerationTarget> ResolveAsync(Guid objectTypeId, string id)
        => await TryResolveAsync(objectTypeId, id) ?? throw new SparkRowLevelAccessDeniedException($"Moderation/{objectTypeId}");

    public async Task<ModerationTarget?> TryResolveAsync(Guid objectTypeId, string id)
    {
        var definition = modelLoader.GetEntityType(objectTypeId);
        var clrType = ResolveType(definition?.ClrType);
        if (definition is null || clrType is null || !typeof(IModeratable).IsAssignableFrom(clrType) || string.IsNullOrEmpty(id))
            return null;

        // The row gate: a row the caller may not read is null here, like one that does not exist.
        if (await databaseAccess.GetPersistentObjectAsync(objectTypeId, id) is null)
            return null;

        // Session-cached: the gated load above already read the document into the request session.
        return await session.LoadAsync(clrType, id) is IModeratable entity
            ? new ModerationTarget(id, definition, clrType, entity)
            : null;
    }

    public static Type? ResolveType(string? clrType) => clrType is null ? null : Types.GetOrAdd(clrType, static name =>
    {
        if (Type.GetType(name) is { } type)
            return type;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.GetType(name) is { } found)
                return found;
        }
        return null;
    });
}
