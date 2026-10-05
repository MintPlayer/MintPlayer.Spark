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
/// Stamps <see cref="IAuditCreated"/> / <see cref="IAuditModified"/> entities, makes a revert exact, and records which attributes a
/// write to a type with revisions changed (<see cref="SparkFacts.ChangedAttributes"/>), for the
/// durable after-commit interceptors.
/// </summary>
/// <remarks>
/// Persistence interceptors (#482; "interceptor" is the older name), run by the framework on every write. The
/// before-save interceptor runs in <see cref="InterceptorStage.Finalize"/>, after every interceptor that trims or stamps
/// fields, so "did this edit change anything?" judges the entity as it will be written. It also runs
/// for a module sync, to observe it, and stamps it with the user the replica states (#271, F2).
/// </remarks>
internal sealed partial class HistoryInterceptor : IBeforeSave, IBeforeDelete
{
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly HistoryRequestState state;
    [Inject] private readonly TimeProvider? timeProvider;
    [Inject] private readonly ISparkSyncInitiator? syncInitiator = null;

    private DateTimeOffset Now => (timeProvider ?? TimeProvider.System).GetUtcNow();

    /// <summary>
    /// A module sync is observed (its changed attributes are recorded), and stamped only with the user
    /// the replica states (#271, F2).
    /// </summary>
    public bool HandlesSync => true;

    /// <summary>After every interceptor that changes fields, so the no-change check sees the final entity.</summary>
    InterceptorStage IBeforeSave.Stage => InterceptorStage.Finalize;

    public bool AppliesTo(Type entityType)
        => typeof(IAuditCreated).IsAssignableFrom(entityType) || typeof(IAuditModified).IsAssignableFrom(entityType)
            || RevisionsEnabled(entityType);

    public ValueTask OnBeforeSaveAsync(SaveContext context)
    {
        Stamp(context);

        // For the durable after-commit interceptors (#482, D17), which get a payload, not the entity. Last, so
        // the stamp counts; only for a type that keeps revisions, as the revision observer it replaces.
        if (RevisionsEnabled(context.EntityType))
            context.Facts[SparkFacts.ChangedAttributes] = string.Join(',', ChangedAttributes(context.EntityType, context.Before, context.Entity));
        return ValueTask.CompletedTask;
    }

    private void Stamp(SaveContext context)
    {
        // A write a replica forwarded runs here under the replica's module certificate, which has no
        // user id. The replica states the user it was made for (#271, F2); a sync that states none is
        // left as the replica sent it, as before.
        var userId = currentUser.Id;
        if (context.Operation == PersistentObjectOperation.Sync)
        {
            if (syncInitiator?.UserId is not { } initiator)
                return;
            userId = initiator;
        }

        if (context.Operation == PersistentObjectOperation.Revert && state.RevertSource is { } source && source.GetType() == context.Entity.GetType())
        {
            MakeRevertExact(context, source);
            // An attribute the caller may not edit was kept out of the save (contributions M2c-2b):
            // when the revision holds something else there, the revert is partial, and says so.
            state.RevertPartial |= context.UnwritableAttributes.Any(path => !SameAt(path, source, context.Entity));
        }

        // An edit that changes nothing of the stored document is not a modification. The common case
        // is a save that only wrote satellite rows (a [Contribution] property: [JsonIgnore]d, stored in
        // side documents): stamping it would rewrite the target, give it a revision and a new etag,
        // and name the contributor as its modifier — what Contributions promises not to do (R3).
        // Asked of the session before stamping, so the stamp itself is not the change it detects.
        var created = context.Entity as IAuditCreated;
        var modified = context.Entity as IAuditModified;
        if (created is null && modified is null)
            return;

        if (context.Operation is PersistentObjectOperation.Save or PersistentObjectOperation.Sync
            && context.Before is not null && !HasChanged(context.Entity))
            return;

        if (created is not null)
        {
            // CreatedBy / CreatedAt are the stored values on every write but the first, whatever was
            // posted or reverted to (authorship cannot be claimed by editing).
            if (context.Before is IAuditCreated stored)
            {
                created.CreatedBy = stored.CreatedBy;
                created.CreatedAt = stored.CreatedAt;
            }
            else
            {
                created.CreatedBy = userId;
                created.CreatedAt = Now;
            }
        }

        if (modified is not null)
        {
            modified.ModifiedBy = userId;
            modified.ModifiedAt = Now;
        }
    }

    public ValueTask OnBeforeDeleteAsync(DeleteContext context)
    {
        if (context.Operation == PersistentObjectOperation.Purge)
            return ValueTask.CompletedTask;

        // A soft delete (a replaced delete) writes a revision: it should say who deleted. A hard delete
        // discards the stamp with the document; a refused one is evicted by IDatabaseAccess.
        if (context.Operation == PersistentObjectOperation.Delete && context.Entity is IAuditModified audited)
        {
            audited.ModifiedBy = currentUser.Id;
            audited.ModifiedAt = Now;
        }

        return ValueTask.CompletedTask;
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

    /// <summary>
    /// Whether <paramref name="path"/> (<c>Title</c>, or <c>Lines.Text</c> for an AsDetail row
    /// attribute) holds the same value on both entities, compared as JSON. A row attribute compares the
    /// sequence of that column across the rows.
    /// </summary>
    private static bool SameAt(string path, object revision, object entity)
        => JsonSerializer.Serialize(ValueAt(path, revision)) == JsonSerializer.Serialize(ValueAt(path, entity));

    private static object? ValueAt(string path, object? target)
    {
        if (target is null)
            return null;

        var dot = path.IndexOf('.');
        var head = dot < 0 ? path : path[..dot];
        var value = target.GetType().GetProperty(head, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(target);
        if (dot < 0)
            return value;

        var rest = path[(dot + 1)..];
        return value is System.Collections.IEnumerable rows and not string
            ? rows.Cast<object?>().Select(row => ValueAt(rest, row)).ToList()
            : ValueAt(rest, value);
    }

    private static TranslatedString Copy(TranslatedString value)
    {
        var copy = new TranslatedString();
        foreach (var (language, text) in value.Translations)
            copy.Translations[language] = text;
        return copy;
    }

    private bool RevisionsEnabled(Type entityType)
        => modelLoader.GetEntityTypeByClrType(entityType.FullName ?? entityType.Name)?.Revisions?.Enabled == true;

    private string? ChangeVectorOf(object entity)
    {
        // Not tracked by the request session (a create, before it is stored): no change vector to report.
        try { return session.Advanced.GetChangeVectorFor(entity); }
        catch (InvalidOperationException) { return null; }
        catch (ArgumentException) { return null; }
    }

    /// <summary>Whether the tracked <paramref name="entity"/> differs from the document the session loaded. Untracked counts as changed.</summary>
    private bool HasChanged(object entity)
    {
        // RavenDB answers false for an entity it does not track; only a tracked one can be unchanged.
        if (ChangeVectorOf(entity) is null)
            return true;
        try { return session.Advanced.HasChanged(entity); }
        catch (InvalidOperationException) { return true; }
        catch (ArgumentException) { return true; }
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
