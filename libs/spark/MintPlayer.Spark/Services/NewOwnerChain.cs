using System.Reflection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Reflection;
using Raven.Client.Documents.Session;
using Raven.Client.Documents.Session.Operations.Lazy;

namespace MintPlayer.Spark.Services;

/// <summary>
/// The owner a Reference picker's <b>Add new</b> hands <c>OnNewAsync</c> as <c>Parent</c>, with its
/// <c>Parent</c> chain (PRD D12).
/// </summary>
/// <remarks>
/// The client posts the owner as it is on screen: a stored page, an unsaved create page, or a retry
/// prompt's virtual object, each with the chain it was opened from. Every level with an id is replaced
/// by the server's own read (<see cref="IDatabaseAccess.GetPersistentObjectAsync"/>: the type's Read
/// right, collection guard, row filter), so a stored level is never the caller's word. A level without
/// an id is scaffolded from the model and carries only the posted values (<see cref="IEffectiveObjectFactory"/>,
/// as a refresh hook's object): input, never authority.
/// <para>
/// The stored levels cost one round trip: a typed lazy load per level, executed together, after which
/// each level's read finds its document in the scoped session (<see cref="DatabaseAccess"/> shares it).
/// </para>
/// </remarks>
internal static class NewOwnerChain
{
    /// <summary>How many levels a posted chain may have, the owner included; deeper is refused.</summary>
    public const int MaxDepth = 8;

    /// <summary>
    /// Resolves <paramref name="posted"/> (the owner, of <paramref name="ownerType"/>) and its chain.
    /// <c>Refused</c> when the chain is too deep, a level names an unknown type, the owner is not of
    /// <paramref name="ownerType"/>, or a stored level cannot be read by the caller; the caller refuses
    /// all of these alike, so none of them answers "does this exist".
    /// </summary>
    public static async Task<(PersistentObject? Owner, bool Refused)> ResolveAsync(
        IModelLoader modelLoader,
        ISparkTypeResolver typeResolver,
        IDatabaseAccess databaseAccess,
        IEffectiveObjectFactory effectiveObjectFactory,
        IAsyncDocumentSession session,
        EntityTypeDefinition ownerType,
        PersistentObject? posted,
        CancellationToken cancellationToken)
    {
        if (posted is null)
            return (null, false);

        var levels = new List<(PersistentObject Posted, EntityTypeDefinition Type)>();
        for (var level = posted; level is not null; level = level.Parent)
        {
            if (levels.Count == MaxDepth)
                return (null, true);

            var type = levels.Count == 0
                ? (level.ObjectTypeId == ownerType.Id ? ownerType : null)
                : modelLoader.GetEntityType(level.ObjectTypeId);
            if (type is null)
                return (null, true);

            levels.Add((level, type));
        }

        // One round trip for every stored level backed by a document.
        var pending = false;
        foreach (var (level, type) in levels)
        {
            if (level.Id is not { Length: > 0 } id || typeResolver.Resolve(type.ClrType) is not { } clrType)
                continue;

            LazyLoad(clrType).Invoke(session.Advanced.Lazily, [id, cancellationToken]);
            pending = true;
        }
        if (pending)
            await session.Advanced.Eagerly.ExecuteAllPendingLazyOperationsAsync(cancellationToken);

        var resolved = new PersistentObject[levels.Count];
        for (var i = 0; i < levels.Count; i++)
        {
            var (level, type) = levels[i];
            if (level.Id is { Length: > 0 } id)
            {
                if (await databaseAccess.GetPersistentObjectAsync(type.Id, id) is not { } stored)
                    return (null, true);
                resolved[i] = stored;
            }
            else
            {
                resolved[i] = effectiveObjectFactory.Build(type, level);
            }
        }

        for (var i = 0; i < resolved.Length - 1; i++)
            resolved[i].Parent = resolved[i + 1];
        resolved[^1].Parent = null;
        return (resolved[0], false);
    }

    /// <summary>
    /// <c>IAsyncLazySessionOperations.LoadAsync&lt;T&gt;(string, CancellationToken)</c>, closed over the
    /// level's CLR type. Typed, so the later typed read of the same id finds an entity of its own type.
    /// </summary>
    private static MethodInfo LazyLoad(Type clrType) =>
        ReflectionCache.GetOrAdd<(string Op, Type Type), MethodInfo>(
            ("NewOwnerChain.LazyLoad", clrType),
            static k => typeof(IAsyncLazySessionOperations)
                .GetMethods()
                .Single(m => m.Name == nameof(IAsyncLazySessionOperations.LoadAsync)
                    && m.IsGenericMethodDefinition
                    && m.GetParameters() is [{ ParameterType: var id }, { ParameterType: var token }]
                    && id == typeof(string) && token == typeof(CancellationToken))
                .MakeGenericMethod(k.Type));
}
