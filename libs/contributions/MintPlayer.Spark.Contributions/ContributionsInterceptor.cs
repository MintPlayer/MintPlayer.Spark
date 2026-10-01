using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Interceptors;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Contributions;

/// <summary>
/// The contributions runtime as a persistent-object interceptor (PRD T6), ordered last
/// (<see cref="PersistentObjectInterceptorOrder.Contributions"/>): SoftDelete has replaced a delete,
/// and Moderation has refused a suspended or locked caller, before anything here writes.
/// </summary>
/// <remarks>
/// Applies to every target type that declares a <see cref="ContributionAttribute"/> property and to
/// every generated contribution type. Writes go into the request session, so they commit atomically
/// with the save or delete that caused them, and a refusal evicts them (F6).
/// </remarks>
internal sealed class ContributionsInterceptor(
    ContributionCatalog catalog,
    IAsyncDocumentSession session,
    ISparkCurrentUser currentUser,
    IServiceProvider services) : IPersistentObjectInterceptor
{
    /// <summary>The contributor id of a system-context write (a migration, a sync) that has no user.</summary>
    internal const string SystemContributorId = "system";

    public int Order => PersistentObjectInterceptorOrder.Contributions;

    public bool AppliesTo(Type entityType)
        => catalog.ForTarget(entityType).Count > 0 || catalog.ForContribution(entityType) is not null;

    public async ValueTask OnAfterMaterializeAsync(MaterializeContext context)
    {
        // Every reason (Load, SaveReload, Before), in the session that loaded the entity.
        foreach (var handler in catalog.ForTarget(context.EntityType))
            await handler.HydrateAsync(context.Entity, context.GetSession());
    }

    public async ValueTask OnBeforeSaveAsync(SaveContext context)
    {
        if (context.Entity is null)
            return;

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
                });
            }
        }

        // A contribution saved as itself — SoftDelete's restore, a moderator's edit — moves its slot's current.
        if (catalog.ForContribution(context.EntityType) is { } contributions)
            await contributions.OnContributionSavedAsync(context.Entity, context.Before, session);
    }

    public async ValueTask OnBeforeDeleteAsync(DeleteContext context)
    {
        // The owner really goes (a hard delete or a purge, not SoftDelete's replacement): its
        // contributions and current documents go with it, in the same commit.
        if (!context.WasReplaced)
            foreach (var handler in catalog.ForTarget(context.EntityType))
                await handler.OnOwnerDeletedAsync(context.Id, session);

        // A contribution hidden (soft delete), deleted or purged: its slot is recomputed without it, in
        // the delete's own commit.
        if (catalog.ForContribution(context.EntityType) is { } contributions)
            await contributions.OnContributionDeletingAsync(context.Entity, context.Id, session);
    }

    private DateTimeOffset Now => (services.GetService(typeof(TimeProvider)) as TimeProvider ?? TimeProvider.System).GetUtcNow();

    private async Task<string> TargetIdAsync(SaveContext context)
    {
        if (!string.IsNullOrEmpty(context.PersistentObject.Id))
            return context.PersistentObject.Id;

        // A create: the contribution ids need the target's. Storing it now is what the base OnSaveAsync
        // does next anyway (its StoreAsync of a tracked entity is a no-op); a refusal evicts it.
        var entity = context.Entity!;
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
