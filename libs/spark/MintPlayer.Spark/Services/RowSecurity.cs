using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Actions;
using Raven.Client.Documents.Session;
using System.Reflection;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Row-level authorization: whether the current principal may act on one specific row.
/// <para>
/// The entity-type check has already passed by the time anything here runs. This is the second
/// layer — ownership, tenancy, any per-row policy — expressed once by an Actions class overriding
/// <c>IsAllowedAsync(action, entity)</c> and enforced on every path that can return that row.
/// </para>
/// <para>
/// "Every path" is the point. The detail and edit paths have always filtered; the query path never
/// did, and Spark's list screens go through the query path. So an entity whose Actions class
/// carefully scoped rows to their owner was correctly protected when opened and disclosed in full
/// on the screen that lists it. The rules cannot live in two places, so both now go through here.
/// </para>
/// </summary>
internal interface IRowSecurity
{
    Task<bool> IsAllowedAsync(Type entityType, string action, object entity);

    /// <summary>
    /// Whether this entity type has a row-level rule at all — i.e. its Actions class overrides
    /// the hook rather than inheriting the permissive default.
    /// <para>
    /// Checked once per query so the strict handling below applies only where someone deliberately
    /// wrote a rule. Types with no rule keep their existing behaviour exactly, which is what makes
    /// failing closed on the unverifiable cases safe to turn on.
    /// </para>
    /// </summary>
    bool HasRowRule(Type entityType);

    /// <summary>
    /// Drops rows the caller may not see.
    /// <para>
    /// <paramref name="resultType"/> may be a projection rather than the stored document. The rule
    /// is written against the document, so the document is what gets loaded and evaluated — a
    /// projection carries only the fields an index stored, and judging ownership from a partial
    /// view is how a filter silently passes everything.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<object>> FilterAsync(
        IAsyncDocumentSession session,
        IReadOnlyList<object> entities,
        Type entityType,
        Type resultType,
        string action);
}

[Register(typeof(IRowSecurity), ServiceLifetime.Scoped)]
internal partial class RowSecurity : IRowSecurity
{
    [Inject] private readonly IActionsResolver actionsResolver;

    public async Task<bool> IsAllowedAsync(Type entityType, string action, object entity)
    {
        var method = ResolveHook(entityType);
        if (method is null) return true;

        var actions = actionsResolver.ResolveForType(entityType);
        var task = (Task)method.Invoke(actions, [action, entity])!;
        await task;
        return (bool)task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    public bool HasRowRule(Type entityType)
    {
        var method = ResolveHook(entityType);
        if (method is null) return false;

        // Declared on the library's base class means nobody overrode it, so the answer is always
        // "allowed" and there is nothing to enforce.
        var declaring = method.DeclaringType;
        return declaring is null
            || !declaring.IsGenericType
            || declaring.GetGenericTypeDefinition() != typeof(DefaultPersistentObjectActions<>);
    }

    public async Task<IReadOnlyList<object>> FilterAsync(
        IAsyncDocumentSession session,
        IReadOnlyList<object> entities,
        Type entityType,
        Type resultType,
        string action)
    {
        if (entities.Count == 0 || !HasRowRule(entityType))
            return entities;

        var projecting = resultType != entityType;

        Func<object, object?>? idGetter = null;
        if (projecting)
        {
            var idProperty = resultType.GetCachedProperty("Id");
            if (idProperty is null || !idProperty.CanRead)
            {
                // A rule exists and there is no way to correlate the projected row back to the
                // document it came from. Nothing can be verified, so nothing is shown — the
                // alternative is disclosing every row of a type whose author asked for the
                // opposite. Loud and empty beats quiet and wrong.
                return [];
            }

            idGetter = AccessorCache.GetGetter(idProperty);
        }

        var visible = new List<object>(entities.Count);
        foreach (var entity in entities)
        {
            var subject = entity;

            if (projecting)
            {
                var id = idGetter!(entity)?.ToString();
                if (string.IsNullOrEmpty(id))
                    continue;

                // The index can name a document that has since been deleted. Unverifiable, and the
                // row should not be on screen regardless.
                var loaded = await session.LoadAsync<object>(id);
                if (loaded is null)
                    continue;

                subject = loaded;
            }

            if (await IsAllowedAsync(entityType, action, subject))
                visible.Add(entity);
        }

        return visible;
    }

    private MethodInfo? ResolveHook(Type entityType)
    {
        var actionsType = actionsResolver.ResolveForType(entityType).GetType();
        return ReflectionCache.GetOrAdd<(string Op, Type Actions, Type Entity), MethodInfo?>(
            ("RowSecurity.IsAllowedAsync", actionsType, entityType),
            static k => k.Actions.GetMethod("IsAllowedAsync", [typeof(string), k.Entity]));
    }
}
