using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Services;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.History;

/// <summary>
/// Stamps <see cref="IAuditable"/> entities, makes a revert exact, and tells
/// <see cref="ISparkRevisionObserver"/>s about every write to a type whose model enables revisions.
/// </summary>
/// <remarks>
/// Runs in <c>IDatabaseAccess</c> (D1) like every interceptor: an Actions class's <c>OnSaveAsync</c>
/// override that skips the base skips the stamping too (documented core behaviour; a warning is
/// logged once per type). After-hooks — the observers — always run.
/// </remarks>
internal sealed partial class HistoryInterceptor : IPersistentObjectInterceptor
{
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly HistoryRequestState state;
    [Inject] private readonly IEnumerable<ISparkRevisionObserver> observers;
    [Inject] private readonly TimeProvider? timeProvider;

    private DateTimeOffset Now => (timeProvider ?? TimeProvider.System).GetUtcNow();

    /// <summary>After SoftDelete, before Moderation (contributions F5).</summary>
    public int Order => PersistentObjectInterceptorOrder.History;

    public bool AppliesTo(Type entityType)
        => typeof(IAuditable).IsAssignableFrom(entityType) || RevisionsEnabled(entityType);

    public ValueTask OnBeforeSaveAsync(SaveContext context)
    {
        if (context.Entity is null)
            return ValueTask.CompletedTask;

        if (context.Before is not null)
            state.SetPrevious(context, ChangeVectorOf(context.Entity));

        // A module sync carries what the owner module stamped.
        if (context.Operation == PersistentObjectOperation.Sync)
            return ValueTask.CompletedTask;

        if (context.Operation == PersistentObjectOperation.Revert && state.RevertSource is { } source && source.GetType() == context.Entity.GetType())
            MakeRevertExact(context, source);

        if (context.Entity is IAuditable audited)
        {
            // CreatedBy / CreatedAt are the stored values on every write but the first, whatever was
            // posted or reverted to (authorship cannot be claimed by editing).
            if (context.Before is IAuditable stored)
            {
                audited.CreatedBy = stored.CreatedBy;
                audited.CreatedAt = stored.CreatedAt;
            }
            else
            {
                audited.CreatedBy = currentUser.Id;
                audited.CreatedAt = Now;
            }

            audited.ModifiedBy = currentUser.Id;
            audited.ModifiedAt = Now;
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask OnAfterSaveAsync(SaveContext context)
    {
        var previous = state.TakePrevious(context);
        if (!ShouldNotify(context.EntityType) || context.Entity is null)
            return;

        var id = IdOf(context.Entity) ?? context.PersistentObject.Id;
        if (string.IsNullOrEmpty(id))
            return;

        await NotifyAsync(new SparkRevisionEvent
        {
            EntityType = context.EntityType,
            Id = id,
            ChangeVector = ChangeVectorOf(context.Entity),
            PreviousChangeVector = previous,
            UserId = currentUser.Id,
            Kind = context.Operation,
            ChangedAttributes = ChangedAttributes(context.EntityType, context.Before, context.Entity),
        });
    }

    public ValueTask OnBeforeDeleteAsync(DeleteContext context)
    {
        if (context.Operation == PersistentObjectOperation.Purge)
            return ValueTask.CompletedTask;

        state.SetPrevious(context, ChangeVectorOf(context.Entity));

        // A soft delete (a replaced delete) writes a revision: it should say who deleted. A hard delete
        // discards the stamp with the document; a refused one is evicted by IDatabaseAccess.
        if (context.Operation == PersistentObjectOperation.Delete && context.Entity is IAuditable audited)
        {
            audited.ModifiedBy = currentUser.Id;
            audited.ModifiedAt = Now;
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask OnAfterDeleteAsync(DeleteContext context)
    {
        var previous = state.TakePrevious(context);
        // A purge removed every revision; there is nothing to observe.
        if (context.IsPurge || !ShouldNotify(context.EntityType))
            return;

        await NotifyAsync(new SparkRevisionEvent
        {
            EntityType = context.EntityType,
            Id = context.Id,
            ChangeVector = context.WasReplaced ? ChangeVectorOf(context.Entity) : null,
            PreviousChangeVector = previous,
            UserId = currentUser.Id,
            Kind = context.Operation,
            ChangedAttributes = [],
        });
    }

    /// <summary>
    /// The base save MERGES a <see cref="TranslatedString"/> per language, so a language added after
    /// the revision would survive a revert of that attribute (measured, spike H3). For every such
    /// attribute the revert actually writes (a protected attribute keeps its stored value — the base
    /// shields it), the revision's translations replace the merged ones.
    /// </summary>
    private void MakeRevertExact(SaveContext context, object source)
    {
        foreach (var attribute in context.PersistentObject.Attributes)
        {
            if (!attribute.IsValueChanged || attribute.Name.Contains('.'))
                continue;

            var property = context.EntityType.GetProperty(attribute.Name, BindingFlags.Public | BindingFlags.Instance);
            if (property is null || property.PropertyType != typeof(TranslatedString) || !property.CanRead || !property.CanWrite)
                continue;

            var reverted = (TranslatedString?)property.GetValue(source);
            property.SetValue(context.Entity, reverted is null ? null : Copy(reverted));
        }
    }

    private static TranslatedString Copy(TranslatedString value)
    {
        var copy = new TranslatedString();
        foreach (var (language, text) in value.Translations)
            copy.Translations[language] = text;
        return copy;
    }

    private bool ShouldNotify(Type entityType) => observers.Any() && RevisionsEnabled(entityType);

    private bool RevisionsEnabled(Type entityType)
        => modelLoader.GetEntityTypeByClrType(entityType.FullName ?? entityType.Name)?.Revisions?.Enabled == true;

    private string? ChangeVectorOf(object entity)
    {
        // Not tracked by the request session (a create, or an OnSaveAsync override using its own
        // session): no change vector to report.
        try { return session.Advanced.GetChangeVectorFor(entity); }
        catch (InvalidOperationException) { return null; }
        catch (ArgumentException) { return null; }
    }

    private static string? IdOf(object entity)
        => entity.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance)?.GetValue(entity)?.ToString();

    private async Task NotifyAsync(SparkRevisionEvent revision)
    {
        foreach (var observer in observers)
            await observer.OnRevisionCreatedAsync(revision);
    }

    /// <summary>The model attributes whose value differs between <paramref name="before"/> (null for a create) and <paramref name="after"/>.</summary>
    private IReadOnlyList<string> ChangedAttributes(Type entityType, object? before, object after)
    {
        var definition = modelLoader.GetEntityTypeByClrType(entityType.FullName ?? entityType.Name);
        if (definition is null)
            return [];

        var changed = new List<string>();
        foreach (var property in ModelProperties(entityType, definition))
        {
            var now = Serialize(property.GetValue(after));
            var was = before is null ? "null" : Serialize(property.GetValue(before));
            if (!string.Equals(now, was, StringComparison.Ordinal))
                changed.Add(property.Name);
        }
        return changed;
    }

    private static string Serialize(object? value) => JsonSerializer.Serialize(value);

    private static readonly ConcurrentDictionary<(Type, Guid), PropertyInfo[]> PropertyCache = new();

    private static PropertyInfo[] ModelProperties(Type entityType, EntityTypeDefinition definition)
        => PropertyCache.GetOrAdd((entityType, definition.Id), key => definition.Attributes
            .Where(a => !a.Name.Contains('.'))
            .Select(a => key.Item1.GetProperty(a.Name, BindingFlags.Public | BindingFlags.Instance))
            .Where(p => p is { CanRead: true } && p.GetIndexParameters().Length == 0)
            .Select(p => p!)
            .ToArray());
}
