using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Moderation.Documents;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Moderation.Services;

/// <summary>
/// Moderation's interceptors on the write pipeline (#482, run by the framework, so nothing can
/// skip them):
/// <list type="bullet">
/// <item>a suspended account cannot write anything (immediate, read from the suspension document);</item>
/// <item><see cref="IModeratable.AuthorId"/> / <see cref="IModeratable.PostedAt"/> are stamped on create and immutable after;</item>
/// <item>a locked target refuses save, revert, restore, delete and purge (400 "locked") for everyone without <c>Lock/T</c>;</item>
/// <item>a new account creating too many posts a day is throttled (429);</item>
/// <item>a moderator's delete of someone else's post reverses the votes it earned, and moderator
/// restore / purge / revert / delete is audited.</item>
/// </list>
/// </summary>
/// <remarks>
/// A module <c>Sync</c> and the system context pass untouched: the owner module already decided.
/// </remarks>
internal sealed partial class ModerationInterceptor : IBeforeSave, IAfterSave, IBeforeDelete, IAfterDelete, IAfterLoad
{
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly IPermissionService permissions;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly ModerationUserState userState;
    [Inject] private readonly ModerationAudit audit;
    [Inject] private readonly IOptions<SparkModerationOptions> options;
    [Inject] private readonly TimeProvider timeProvider;

    /// <summary>Every type: the suspension write block is not limited to moderatable content.</summary>
    public bool AppliesTo(Type entityType) => true;

    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    public async ValueTask OnBeforeSaveAsync(SaveContext context)
    {
        if (context.Operation == PersistentObjectOperation.Sync || context.IsSystemContext)
            return;

        await RefuseSuspendedAsync();

        if (context.Entity is not IModeratable entity)
            return;

        if (context.Operation == PersistentObjectOperation.New)
        {
            entity.AuthorId = currentUser.IsAuthenticated ? currentUser.Id : null;
            entity.PostedAt = timeProvider.GetUtcNow();
            await ThrottleNewAccountAsync();
            return;
        }

        // Owned by the framework: whatever the client posted, the stored author stays (reputation
        // theft otherwise), and a revert cannot hand a post to its earlier author either.
        if (context.Before is IModeratable stored)
        {
            entity.AuthorId = stored.AuthorId;
            entity.PostedAt = stored.PostedAt;
        }

        if (context.PersistentObject.Id is { Length: > 0 } id)
            await RefuseLockedAsync(context.EntityType, id);
    }

    public async ValueTask OnBeforeDeleteAsync(DeleteContext context)
    {
        if (context.Operation == PersistentObjectOperation.Sync || context.IsSystemContext)
            return;

        await RefuseSuspendedAsync();

        if (context.Entity is not IModeratable entity)
            return;

        await RefuseLockedAsync(context.EntityType, context.Id);

        // "Content deleted by a moderator reverses its votes" (§3.12): decided here, where the author
        // and the actor are known, and done durably after the commit (ModerationVoteReversal).
        var actor = currentUser.IsAuthenticated ? currentUser.Id : null;
        if (actor is not null && actor != entity.AuthorId && options.Value.ReverseVotesOnModeratorDelete)
            context.Facts[ModerationVoteReversal.ReverseVotesFact] = "true";
    }

    public async ValueTask OnAfterSaveAsync(SaveContext context)
    {
        if (context.IsSystemContext || context.Entity is not IModeratable entity)
            return;
        if (context.Operation is PersistentObjectOperation.Revert or PersistentObjectOperation.Restore)
        {
            await audit.WriteAsync(context.Operation.ToString().ToLowerInvariant(), context.PersistentObject.Id,
                TypeName(context.EntityType), entity.AuthorId);
        }
    }

    public async ValueTask OnAfterDeleteAsync(DeleteContext context)
    {
        if (context.Operation == PersistentObjectOperation.Sync || context.IsSystemContext || context.Entity is not IModeratable entity)
            return;

        var actor = currentUser.IsAuthenticated ? currentUser.Id : null;
        var byOther = actor is not null && actor != entity.AuthorId;
        if (!byOther && !context.IsPurge)
            return;

        var typeName = TypeName(context.EntityType);
        await audit.WriteAsync(context.IsPurge ? "purge" : "delete", context.Id, typeName, entity.AuthorId);
    }

    public async ValueTask OnAfterLoadAsync(LoadContext context)
    {
        // A UI hint only — enforcement is the before-interceptors above. A locked post shows no Edit/Delete
        // to those the lock binds.
        if (context.IsSystemContext || context.PersistentObject.Id is not { Length: > 0 } id)
            return;
        if (!typeof(IModeratable).IsAssignableFrom(context.EntityType))
            return;
        if (await IsLockedForCallerAsync(context.EntityType, id))
            ((IDisablable)context.PersistentObject).DisableActions("Edit", "Delete");
    }

    /// <summary>
    /// The checks a satellite write gets (<see cref="ModerationSatelliteWriteGuard"/>): the suspension of
    /// the caller, and a lock on the written document when its type is <see cref="IModeratable"/>. The
    /// target's own lock was already judged by <see cref="OnBeforeSaveAsync"/> of the target's save.
    /// </summary>
    internal async Task EnsureMayWriteSatelliteAsync(SatelliteWriteContext context)
    {
        await RefuseSuspendedAsync();
        await RefuseLockedAsync(context.DocumentType, context.DocumentId);
    }

    private async Task RefuseSuspendedAsync()
    {
        if (await userState.IsCurrentUserSuspendedAsync())
            throw new SparkValidationException("Your account is suspended.");
    }

    private async Task RefuseLockedAsync(Type entityType, string id)
    {
        if (await IsLockedForCallerAsync(entityType, id))
            throw new SparkValidationException("This post is locked.");
    }

    private async Task<bool> IsLockedForCallerAsync(Type entityType, string id)
    {
        if (!typeof(IModeratable).IsAssignableFrom(entityType))
            return false;
        using var check = documentStore.OpenAsyncSession();
        if (!await check.Advanced.ExistsAsync(ModerationIds.Lock(id)))
            return false;
        // Moderators of the type are exempt.
        return !await permissions.IsAllowedAsync(ModerationRights.Lock, TypeName(entityType));
    }

    /// <summary>New-account throttle (§3.12): 429 once an account younger than AccountAgeDays created MaxPostsPerDay posts today.</summary>
    private async Task ThrottleNewAccountAsync()
    {
        var snapshot = await userState.GetCurrentAsync();
        if (snapshot is null)
            return;
        var limits = options.Value.NewAccounts;
        if (snapshot.AgeDays >= limits.AccountAgeDays)
            return;

        var now = UtcNow;
        var counterId = ModerationIds.PostCounter(snapshot.UserId, ModerationIds.Day(now));
        // On the request session: committed in the same transaction as the post it counts.
        var counter = await session.LoadAsync<ModerationCounter>(counterId) ?? new ModerationCounter();
        if (counter.Count >= limits.MaxPostsPerDay)
            throw new SparkThrottledException($"New accounts may post {limits.MaxPostsPerDay} times a day.", now.Date.AddDays(1) - now);
        counter.Count++;
        await session.StoreAsync(counter, counterId);
        session.Advanced.GetMetadataFor(counter)["@expires"] = now.Date.AddDays(2).ToString("O");
    }

    private string TypeName(Type entityType)
        => modelLoader.GetEntityTypeByClrType(entityType.FullName!)?.Name ?? entityType.Name;
}
