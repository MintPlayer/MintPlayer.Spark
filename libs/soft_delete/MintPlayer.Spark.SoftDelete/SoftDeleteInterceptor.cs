using System.Collections;
using System.Reflection;
using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.Revisions;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.SoftDelete;

/// <summary>
/// Turns a delete of an <see cref="ISoftDeletable"/> into setting its fields, owns those fields on
/// every other write, refuses references to deleted rows, finishes a purge by deleting the row's
/// revisions, and tells <see cref="ISoftDeleteObserver"/>s.
/// </summary>
/// <remarks>
/// <para>
/// Runs in <c>IDatabaseAccess</c> (D1), so an Actions class's <c>OnDeleteAsync</c> override cannot
/// defeat the replacement: for a soft-deletable type that override is not called on a delete — only
/// on a purge (the startup check warns about such overrides).
/// </para>
/// <para>
/// A <c>Sync</c> (a write replicated from the owner module) passes through untouched: the owner
/// already decided, and its soft delete arrives here as a save.
/// </para>
/// </remarks>
internal sealed partial class SoftDeleteInterceptor : IPersistentObjectInterceptor
{
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly SoftDeleteRequestState state;
    [Inject] private readonly IEnumerable<ISoftDeleteObserver> observers;
    [Inject] private readonly ILogger<SoftDeleteInterceptor> logger;
    [Inject] private readonly TimeProvider? timeProvider;

    public bool AppliesTo(Type entityType) => typeof(ISoftDeletable).IsAssignableFrom(entityType);

    private DateTimeOffset Now => (timeProvider ?? TimeProvider.System).GetUtcNow();

    public ValueTask OnBeforeDeleteAsync(DeleteContext context)
    {
        // A purge must delete; a module sync is the owner's decision. Only a caller's delete is softened.
        if (context.Operation != PersistentObjectOperation.Delete || context.Entity is not ISoftDeletable entity)
            return ValueTask.CompletedTask;

        entity.IsDeleted = true;
        entity.DeletedAt = Now;
        entity.DeletedBy = currentUser.Id;
        entity.DeleteReason = state.PendingReason;
        context.Replace();
        return ValueTask.CompletedTask;
    }

    public async ValueTask OnAfterDeleteAsync(DeleteContext context)
    {
        if (context.WasReplaced)
        {
            var entity = (ISoftDeletable)context.Entity;
            await NotifyAsync(context.EntityType, context.Id, entity.DeleteReason, static (o, e) => o.OnDeletedAsync(e));
            return;
        }

        if (!context.IsPurge)
            return;

        // An Actions class whose OnDeleteAsync override did not actually delete (it soft-deleted by
        // hand, say) must not have the history of a row that still exists wiped.
        using (var check = documentStore.OpenAsyncSession())
        {
            if (await check.Advanced.ExistsAsync(context.Id))
                throw new InvalidOperationException(
                    $"Purge of '{context.Id}' did not delete the document: the {context.EntityType.Name} Actions class " +
                    "overrides OnDeleteAsync without deleting it. Its revisions were kept.");
        }

        // After the document, never before: deleting revisions first and the document second writes a
        // fresh delete revision (measured, #460 spike H1). Force-created revisions only go with the flag.
        var result = await documentStore.Maintenance.SendAsync(new DeleteRevisionsOperation(context.Id, removeForceCreatedRevisions: true));
        logger.LogInformation("Purged {EntityType} {Id} and {Revisions} revision(s).", context.EntityType.Name, context.Id, result.TotalDeletes);

        state.Purged.Add(context.Id);
        await NotifyAsync(context.EntityType, context.Id, null, static (o, e) => o.OnPurgedAsync(e));
    }

    public async ValueTask OnBeforeSaveAsync(SaveContext context)
    {
        if (context.Operation == PersistentObjectOperation.Sync || context.Entity is not ISoftDeletable entity)
            return;

        if (context.Operation == PersistentObjectOperation.Restore)
        {
            entity.IsDeleted = false;
            entity.DeletedAt = null;
            entity.DeletedBy = null;
            entity.DeleteReason = null;
        }
        else if (context.Before is ISoftDeletable stored)
        {
            // An edit (or a revert) cannot delete, undelete or re-attribute a row: the fields are the
            // framework's, whatever the client posted.
            entity.IsDeleted = stored.IsDeleted;
            entity.DeletedAt = stored.DeletedAt;
            entity.DeletedBy = stored.DeletedBy;
            entity.DeleteReason = stored.DeleteReason;
        }
        else
        {
            entity.IsDeleted = false;
            entity.DeletedAt = null;
            entity.DeletedBy = null;
            entity.DeleteReason = null;
        }

        await RefuseReferencesToDeletedRowsAsync(context, context.Entity!);
    }

    public async ValueTask OnAfterSaveAsync(SaveContext context)
    {
        if (context.Operation == PersistentObjectOperation.Restore && context.PersistentObject.Id is { } id)
            await NotifyAsync(context.EntityType, id, null, static (o, e) => o.OnRestoredAsync(e));
    }

    public async ValueTask OnNaturalIdCollisionAsync(NaturalIdCollisionContext context)
    {
        if (context.Existing is not ISoftDeletable { IsDeleted: true })
            return;

        // Explain only to a caller entitled to know the row exists and is deleted; everyone else keeps
        // the 404 core answers.
        var typeName = SoftDeleteTypeNames.Of(modelLoader, context.EntityType);
        if (!context.IsSystemContext
            && !await permissionService.IsAllowedAsync(SoftDeleteRights.ViewDeleted, typeName)
            && !await permissionService.IsAllowedAsync(SoftDeleteRights.Restore, typeName))
            return;

        throw new SparkValidationException(
            $"A deleted {typeName} already holds this key ({context.Id}). Restore it instead of creating a new one.");
    }

    /// <summary>
    /// Core does not row-check references on save (D1 gap), so a caller could point a reference at a
    /// row the reference picker never offered — a deleted one. Refused (400) unless the caller holds
    /// <c>ViewDeleted</c> on the target type. Only references this save changes are checked: an
    /// existing reference to a row deleted since stays saveable.
    /// </summary>
    private async Task RefuseReferencesToDeletedRowsAsync(SaveContext context, object entity)
    {
        var references = SoftDeleteReferences.For(context.EntityType);
        if (references.Length == 0)
            return;

        foreach (var reference in references)
        {
            var added = reference.Ids(entity);
            if (added.Count == 0)
                continue;
            if (context.Before is not null)
                added.ExceptWith(reference.Ids(context.Before));
            if (added.Count == 0)
                continue;

            var deleted = await LoadDeletedAsync(reference.TargetType, added);
            if (deleted.Count == 0)
                continue;

            var targetName = SoftDeleteTypeNames.Of(modelLoader, reference.TargetType);
            if (context.IsSystemContext || await permissionService.IsAllowedAsync(SoftDeleteRights.ViewDeleted, targetName))
                continue;

            throw new SparkValidationException(
                $"'{reference.Property.Name}' refers to a deleted {targetName}.", reference.Property.Name);
        }
    }

    private async Task<IReadOnlyList<string>> LoadDeletedAsync(Type targetType, IReadOnlyCollection<string> ids)
    {
        var load = LoadDeletedMethod.MakeGenericMethod(targetType);
        using var session = documentStore.OpenAsyncSession();
        return await (Task<IReadOnlyList<string>>)load.Invoke(null, [session, ids])!;
    }

    private static readonly MethodInfo LoadDeletedMethod =
        typeof(SoftDeleteInterceptor).GetMethod(nameof(LoadDeletedTypedAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    // Typed, never LoadAsync<object>: an untyped load of a document without @Raven-Clr-Type returns a
    // JObject, which is not ISoftDeletable and would read as "not deleted".
    private static async Task<IReadOnlyList<string>> LoadDeletedTypedAsync<T>(IAsyncDocumentSession session, IReadOnlyCollection<string> ids)
        where T : class
    {
        var loaded = await session.LoadAsync<T>(ids);
        return loaded.Where(pair => pair.Value is ISoftDeletable { IsDeleted: true }).Select(pair => pair.Key).ToList();
    }

    private async Task NotifyAsync(Type entityType, string id, string? reason, Func<ISoftDeleteObserver, SoftDeleteEvent, ValueTask> call)
    {
        SoftDeleteEvent? e = null;
        foreach (var observer in observers)
        {
            e ??= new SoftDeleteEvent
            {
                EntityType = entityType,
                Id = id,
                UserId = currentUser.Id,
                Reason = reason,
                OccurredAt = Now,
            };
            await call(observer, e);
        }
    }
}

/// <summary>The <c>[Reference]</c> properties of a type that point at a soft-deletable type.</summary>
internal sealed record SoftDeleteReference(PropertyInfo Property, Type TargetType)
{
    /// <summary>The ids this property holds on <paramref name="entity"/> (a string, or a collection of strings).</summary>
    public HashSet<string> Ids(object entity)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        switch (Property.GetValue(entity))
        {
            case string id when id.Length > 0:
                ids.Add(id);
                break;
            case IEnumerable values and not string:
                foreach (var value in values)
                    if (value is string { Length: > 0 } item)
                        ids.Add(item);
                break;
        }
        return ids;
    }
}

internal static class SoftDeleteReferences
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, SoftDeleteReference[]> Cache = new();

    public static SoftDeleteReference[] For(Type entityType) => Cache.GetOrAdd(entityType, static type => type
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Select(p => (Property: p, Attribute: p.GetCustomAttribute<ReferenceAttribute>()))
        .Where(x => x.Attribute is not null
                    && typeof(ISoftDeletable).IsAssignableFrom(x.Attribute.TargetType)
                    && x.Property.CanRead
                    && (x.Property.PropertyType == typeof(string) || typeof(IEnumerable<string>).IsAssignableFrom(x.Property.PropertyType)))
        .Select(x => new SoftDeleteReference(x.Property, x.Attribute!.TargetType))
        .ToArray());
}
