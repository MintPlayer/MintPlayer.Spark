using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Interceptors;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Contributions;

/// <summary>
/// The contributions runtime as persistence interceptors (PRD T6; #482, "interceptor" is the older name).
/// SoftDelete's replacement is decided before any before-delete interceptor (<see cref="DeleteContext.IsReplaced"/>
/// is final here), and a refusal by any other interceptor — Moderation's suspension or lock — evicts what
/// these interceptors wrote, so their relative order does not matter.
/// </summary>
/// <remarks>
/// Applies to every target type that declares a <see cref="ContributionAttribute"/> property and to
/// every generated contribution and current type. Writes go into the request session, so they commit
/// atomically with the save or delete that caused them, and a refusal evicts them (F6).
/// </remarks>
internal sealed class ContributionsInterceptor(
    ContributionCatalog catalog,
    IAsyncDocumentSession session,
    ISparkCurrentUser currentUser,
    ContributionRequestState state,
    IServiceProvider services) : IAfterMaterialize, IAfterLoad, IBeforeSave, IAfterSave, IBeforeDelete, IAfterDelete
{
    /// <summary>The contributor id of a system-context write (a migration, a sync) that has no user.</summary>
    internal const string SystemContributorId = "system";

    /// <summary>The audit action of a removed version (Delete on the current type).</summary>
    internal const string RemoveVersionAuditAction = "RemoveContributionVersion";

    /// <summary>A module sync moves a slot's current and removes a deleted owner's contributions too.</summary>
    public bool HandlesSync => true;

    public bool AppliesTo(Type entityType)
        => catalog.ForTarget(entityType).Count > 0
           || catalog.ForContribution(entityType) is not null
           || catalog.ForCurrent(entityType) is not null;

    public async ValueTask OnAfterMaterializeAsync(MaterializeContext context)
    {
        // Every reason (Load, SaveReload, Before), in the session that loaded the entity. Names only
        // for what is shown: the Before side load is diffed, never presented.
        var names = context.Reason == MaterializeReason.Before ? null : services;
        var rows = context.Reason == MaterializeReason.Before ? null : state;
        foreach (var handler in catalog.ForTarget(context.EntityType))
            await handler.HydrateAsync(context.Entity, context.GetSession(), names, rows);
    }

    /// <summary>
    /// Marks each row of a contribution property with whether the version it shows is the caller's own
    /// (<c>metadata.contribution.own</c>), so ng-spark's conflict merge (M1c) can tell a second tab of
    /// the same user (a true conflict) from another contributor's version (theirs wins). A boolean, never
    /// the contributor id: the raw ids stay off the target's page.
    /// </summary>
    public ValueTask OnAfterLoadAsync(LoadContext context)
    {
        var po = context.PersistentObject;
        if (po.Id is not { Length: > 0 } targetId)
            return ValueTask.CompletedTask;
        var me = currentUser.Id;
        foreach (var handler in catalog.ForTarget(context.EntityType))
        {
            var property = handler.Descriptor.PropertyName;
            if (!state.RowContributors.TryGetValue((targetId, property), out var contributors))
                continue;
            if (po.Attributes.FirstOrDefault(a => a.Name == property) is not PersistentObjectAttributeAsDetail attribute)
                continue;
            IReadOnlyList<PersistentObject> rows = attribute.Objects
                ?? (attribute.Object is { } single ? new[] { single } : Array.Empty<PersistentObject>());
            foreach (var row in rows)
            {
                var key = handler.Descriptor.IsCollection ? row.Id ?? "" : "";
                if (!contributors.TryGetValue(key, out var contributor))
                    continue;
                row.Metadata ??= [];
                row.Metadata[ContributionRowMetadataKey] = new Dictionary<string, object?>
                {
                    ["own"] = me is { Length: > 0 } && string.Equals(contributor, me, StringComparison.Ordinal),
                };
            }
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>The <see cref="PersistentObject.Metadata"/> key of a contribution row's facts.</summary>
    internal const string ContributionRowMetadataKey = "contribution";

    public async ValueTask OnBeforeSaveAsync(SaveContext context)
    {
        // Only an edit or a create carries contribution rows. A revert and a sync never post them (F2),
        // a restore posts nothing, and diffing those would read "every row removed".
        if (context.Operation is PersistentObjectOperation.New or PersistentObjectOperation.Save)
        {
            foreach (var handler in catalog.ForTarget(context.EntityType))
            {
                var name = handler.Descriptor.PropertyName;
                // Not posted, or dropped by the attribute-write shield: the entity keeps its hydrated rows.
                if (!context.PersistentObject.Attributes.Any(a => string.Equals(a.Name, name, StringComparison.Ordinal))
                    || context.UnwritableAttributes.Contains(name, StringComparer.OrdinalIgnoreCase))
                    continue;

                await handler.OnOwnerSaveAsync(new OwnerSave
                {
                    Context = context,
                    Session = session,
                    TargetIdAsync = () => TargetIdAsync(context),
                    UserId = () => ContributorId(context),
                    Now = Now,
                    IsSystemContext = context.IsSystemContext,
                    Services = services,
                    User = context.User,
                    State = state,
                });
            }
        }

        // A contribution saved as itself — SoftDelete's restore, a moderator's edit — moves its slot's current.
        if (catalog.ForContribution(context.EntityType) is { } contributions)
            await contributions.OnContributionSavedAsync(context.Entity, context.Before, session);
    }

    /// <summary>After the commit: the Q3 notices reach the save response's <c>operations</c>.</summary>
    public ValueTask OnAfterSaveAsync(SaveContext context)
    {
        state.FlushNotices(services);
        return ValueTask.CompletedTask;
    }

    public async ValueTask OnBeforeDeleteAsync(DeleteContext context)
    {
        // The owner really goes (a hard delete or a purge, not SoftDelete's replacement): its
        // contributions and current documents go with it, in the same commit.
        if (!context.IsReplaced)
            foreach (var handler in catalog.ForTarget(context.EntityType))
                await handler.OnOwnerDeletedAsync(context.Id, session);

        // A contribution hidden (soft delete), deleted or purged: its slot is recomputed without it, in
        // the delete's own commit.
        if (catalog.ForContribution(context.EntityType) is { } contributions)
            await contributions.OnContributionDeletingAsync(context.Entity, context.Id, session);

        // A moderator removes a whole version (Delete on the current type, PRD Q3/Q4): every visible
        // contribution of the slot is hidden in this commit, then the base delete removes the current
        // document. Audited after the commit.
        if (catalog.ForCurrent(context.EntityType) is { } currents)
        {
            var write = ModeratorWrite(context.IsSystemContext, context.User);
            var affected = await currents.OnCurrentDeletingAsync(context.Entity, context.Id, write);
            state.Audits.Add(new SatelliteAuditEntry
            {
                Action = RemoveVersionAuditAction,
                DocumentType = currents.Descriptor.ContributionType,
                DocumentId = context.Id,
                AffectedDocumentIds = affected,
                TargetType = currents.Descriptor.TargetType,
                TargetId = currents.TargetIdOfCurrent(context.Id) ?? "",
                Reason = currents.Descriptor.IsSoftDeletable ? SoftDeleteBridge.VersionRemovedReason : null,
                User = context.User,
            });
        }
    }

    public async ValueTask OnAfterDeleteAsync(DeleteContext context)
        => await state.FlushAuditsAsync(services);

    private DateTimeOffset Now => (services.GetService(typeof(TimeProvider)) as TimeProvider ?? TimeProvider.System).GetUtcNow();

    /// <summary>A moderator write in the request session, as the caller (guards skipped for the system context).</summary>
    internal ModeratorWrite ModeratorWrite(bool isSystemContext, System.Security.Claims.ClaimsPrincipal? user) => new()
    {
        Session = session,
        UserId = currentUser.Id is { Length: > 0 } id ? id : SystemContributorId,
        Now = Now,
        Guards = isSystemContext ? [] : [.. services.GetServices<ISatelliteWriteGuard>()],
        User = user,
    };

    private async Task<string> TargetIdAsync(SaveContext context)
    {
        if (!string.IsNullOrEmpty(context.PersistentObject.Id))
            return context.PersistentObject.Id;

        // A create: the contribution ids need the target's. Storing it now is what the
        // framework does next anyway (its StoreAsync of a tracked entity is a no-op); a refusal evicts it.
        var entity = context.Entity;
        var id = session.Advanced.GetDocumentId(entity);
        if (string.IsNullOrEmpty(id))
        {
            await session.StoreAsync(entity);
            id = session.Advanced.GetDocumentId(entity);
        }
        if (string.IsNullOrEmpty(id) || id.EndsWith('/') || id.EndsWith('|'))
            throw new InvalidOperationException(
                $"Contributions need the id of a new {context.EntityType.Name} before it is saved, and the id convention gave '{id}' (a server-assigned id). Use a client-side id convention (HiLo, a natural id) for targets with [Contribution] properties.");
        return id;
    }

    private string ContributorId(SaveContext context)
    {
        // The same id History stamps (ModifiedBy) and Moderation keys accounts on.
        if (currentUser.Id is { Length: > 0 } id)
            return id;
        if (context.IsSystemContext)
            return SystemContributorId;
        throw new SparkValidationException("Sign in to contribute.");
    }
}
