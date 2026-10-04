using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;

namespace MintPlayer.Spark.Replication.Services;

/// <summary>
/// Forwards every committed write of a <c>[Replicated]</c> type to its owner module (#482, D32: replication
/// adopts the interceptor system). A replaced delete (SoftDelete) is forwarded as the save it became — a hard
/// delete sent for a soft one would destroy the owner's copy.
/// </summary>
/// <remarks>
/// An after-interceptor, so it only ever forwards a committed change. Isolated like every after-interceptor: a failed
/// dispatch is logged and leaves the local write standing.
/// </remarks>
internal sealed partial class ReplicationInterceptor : IAfterSave, IAfterDelete
{
    [Inject] private readonly ISyncActionInterceptor syncActions;

    public bool AppliesTo(Type entityType) => syncActions.IsReplicated(entityType);

    /// <summary>As before #482: a write that arrived as a sync is forwarded too (the owner ignores its own echo).</summary>
    public bool HandlesSync => true;

    public async ValueTask OnAfterSaveAsync(SaveContext context)
        => await syncActions.HandleSaveAsync(context.EntityType, context.PersistentObject, isNew: context.IsNew);

    public async ValueTask OnAfterDeleteAsync(DeleteContext context)
    {
        if (context.IsReplaced)
            await syncActions.HandleSaveAsync(context.Entity, context.Id, isNew: false);
        else
            await syncActions.HandleDeleteAsync(context.EntityType, context.Id);
    }
}
