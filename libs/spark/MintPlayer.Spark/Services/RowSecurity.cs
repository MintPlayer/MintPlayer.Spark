using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Actions;
using Raven.Client.Documents.Session;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Row-level authorization: whether the current principal may act on one specific row.
/// <para>
/// The entity-type check has already passed by the time anything here runs. This is the second
/// layer — ownership, tenancy, any per-row policy — expressed once by an Actions class and
/// enforced on every path that can return that row.
/// </para>
/// <para>
/// An Actions class states its policy through two optional members, and this class defines how
/// they derive rather than interact. Only <c>GetRowFilterAsync</c> overridden: the expression composes
/// into the RavenDB query where shapes allow, and single-row checks compile the same expression —
/// one source of truth, list and detail cannot diverge. Only <c>IsAllowedAsync</c> overridden:
/// post-materialization filtering, exactly the original behavior. Both: AND semantics — the
/// filter narrows, the predicate refines.
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
    /// <c>IsAllowedAsync</c> or <c>GetRowFilterAsync</c> rather than inheriting the permissive defaults.
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

    /// <summary>
    /// Pushes the type's row filter into the query itself, when there is one and the shapes allow
    /// it — <paramref name="elementType"/> must be the entity type the filter is written against.
    /// When the query yields an index projection instead, this is a no-op with a diagnostic and
    /// <see cref="FilterAsync"/> remains the enforcement point (batched reload) — the pushdown is
    /// an optimization, never the only gate.
    /// </summary>
    Task<object> ComposeRowFilterAsync(object queryable, Type entityType, Type elementType, string action);

    /// <summary>
    /// Per-viewer attribute redaction (#236 G4): asks the Actions class which attributes of each
    /// row this caller must not see, and nulls them out of the mapped payload — value gone,
    /// attribute invisible, embedded AsDetail rows included. Redacts rather than omits: dropping
    /// the attribute would break name-indexed clients and leak the rule via schema mismatch.
    /// <para>
    /// Rows may be projections; the hook is typed on the entity, so base documents are batch
    /// loaded (a session-cache hit on every path where <see cref="FilterAsync"/> already ran).
    /// A projected row whose base document can't be found gets everything redacted — unverifiable
    /// is not shown. Zero cost for types that don't override the hook.
    /// </para>
    /// </summary>
    Task RedactAsync(
        IAsyncDocumentSession session,
        IReadOnlyList<(Abstractions.PersistentObject Po, object Row)> items,
        Type entityType,
        Type resultType,
        string action);
}

[Register(typeof(IRowSecurity), ServiceLifetime.Scoped)]
internal partial class RowSecurity : IRowSecurity
{
    [Inject] private readonly IActionsResolver actionsResolver;
    [Inject] private readonly ILogger<RowSecurity>? logger;
    [Inject] private readonly Microsoft.AspNetCore.Http.IHttpContextAccessor? httpContextAccessor;

    /// <summary>Types whose row-security mode has been announced, so the diagnostic logs once.</summary>
    private static readonly ConcurrentDictionary<(Type Type, string Note), bool> announced = new();

    // Per-request memo (#239 M2). RowSecurity is Scoped, so these live exactly one request — one
    // connection for a stream, cleared on the re-auth tick (ResetRequestFilterCache, M3). They bound
    // hook invocations to distinct (type, action) pairs per request, independent of row/page/batch
    // count — the property that keeps an async, I/O-doing hook clear of RavenDB's 30-request cap
    // (the breadcrumb loop alone would otherwise re-invoke it per referenced document). Plain
    // Dictionary, not Concurrent: a request scope has no internal parallelism in the row paths.
    private readonly Dictionary<(Type EntityType, string Action), Task<LambdaExpression?>> filterExpressionMemo = new();
    private readonly Dictionary<(Type EntityType, string Action), Delegate?> compiledFilterMemo = new();

    public async Task<bool> IsAllowedAsync(Type entityType, string action, object entity)
    {
        var rule = await ResolveEffectiveRuleAsync(entityType, action);
        return rule is null || await rule(entity);
    }

    public bool HasRowRule(Type entityType)
        => IsOverridden(ResolveHook(entityType)) || IsOverridden(ResolveFilterHook(entityType));

    public async Task<IReadOnlyList<object>> FilterAsync(
        IAsyncDocumentSession session,
        IReadOnlyList<object> entities,
        Type entityType,
        Type resultType,
        string action)
    {
        if (entities.Count == 0)
            return entities;

        // Per-request: a GetRowFilterAsync override returning null (an administrator, say) means no
        // restriction for this caller, even though the type declares a rule.
        var rule = await ResolveEffectiveRuleAsync(entityType, action);
        if (rule is null)
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

        // One batched load for the whole page, not one per row. The request session caps requests
        // (MaxNumberOfRequestsPerSession, default 30) precisely to catch the per-row shape — a
        // projection-backed query over a row-scoped type would throw past ~29 rows.
        Dictionary<string, object>? baseDocuments = null;
        if (projecting)
        {
            var ids = entities
                .Select(e => idGetter!(e)?.ToString())
                .Where(id => !string.IsNullOrEmpty(id))
                .Cast<string>()
                .ToList();

            baseDocuments = ids.Count > 0
                ? await session.LoadAsync<object>(ids)
                : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
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
                if (!baseDocuments!.TryGetValue(id, out var loaded) || loaded is null)
                    continue;

                subject = loaded;
            }

            if (await rule(subject))
                visible.Add(entity);
        }

        return visible;
    }

    public async Task<object> ComposeRowFilterAsync(object queryable, Type entityType, Type elementType, string action)
    {
        // Same exemption as ResolveEffectiveRuleAsync: the system is not a viewer to scope rows for.
        if (Abstractions.Authentication.SparkSystemContext.IsSystemContext(httpContextAccessor))
            return queryable;

        var filter = await InvokeGetRowFilterAsync(entityType, action);
        if (filter is null)
            return queryable;

        // A constant predicate ("x => false" for a caller who may see nothing) needs no query
        // translation — and RavenDB's provider may not survive one. The compiled post-filter in
        // FilterAsync evaluates it in memory instead.
        if (filter.Body is ConstantExpression)
            return queryable;

        if (elementType != entityType)
        {
            // The filter is typed on the entity; it cannot compose into a projection query.
            // FilterAsync stays the gate (batched base-document reload) — filtered, just not
            // pushed down. Announced once so an O(collection) query on a large type is visible.
            if (announced.TryAdd((entityType, $"fallback:{elementType.Name}"), true))
            {
                logger?.LogInformation(
                    "Row filter for {EntityType} cannot compose into projection {ProjectionType}; "
                    + "falling back to post-materialization filtering with a batched reload.",
                    entityType.Name, elementType.Name);
            }
            return queryable;
        }

        if (announced.TryAdd((entityType, "pushdown"), true))
        {
            logger?.LogInformation(
                "Row security for {EntityType}: filter expression composes into the database query{Refinement}.",
                entityType.Name,
                IsOverridden(ResolveHook(entityType)) ? " with IsAllowedAsync as per-row refinement" : "");
        }

        var whereMethod = ReflectionCache.GetOrAdd<(string Op, Type Entity), MethodInfo>(
            ("RowSecurity.QueryableWhere", entityType),
            static k => typeof(Queryable).GetMethods()
                .First(m => m.Name == nameof(Queryable.Where)
                    && m.GetParameters().Length == 2
                    // Expression<Func<T,bool>>, not the indexed Expression<Func<T,int,bool>> overload.
                    && m.GetParameters()[1].ParameterType.GetGenericArguments()[0].GetGenericArguments().Length == 2)
                .MakeGenericMethod(k.Entity));

        return whereMethod.Invoke(null, [queryable, filter])!;
    }

    public async Task RedactAsync(
        IAsyncDocumentSession session,
        IReadOnlyList<(Abstractions.PersistentObject Po, object Row)> items,
        Type entityType,
        Type resultType,
        string action)
    {
        if (items.Count == 0)
            return;

        var hook = ResolveProtectedHook(entityType);
        if (!IsOverridden(hook))
            return;

        // Sync and background work must see (and replicate) full values.
        if (Abstractions.Authentication.SparkSystemContext.IsSystemContext(httpContextAccessor))
            return;

        var actions = actionsResolver.ResolveForType(entityType);
        var projecting = resultType != entityType;

        Func<object, object?>? idGetter = null;
        Dictionary<string, object>? baseDocuments = null;
        if (projecting)
        {
            var idProperty = resultType.GetCachedProperty("Id");
            idGetter = idProperty is not null && idProperty.CanRead
                ? AccessorCache.GetGetter(idProperty)
                : null;

            var ids = idGetter is null
                ? []
                : items
                    .Select(i => idGetter(i.Row)?.ToString())
                    .Where(id => !string.IsNullOrEmpty(id))
                    .Cast<string>()
                    .ToList();

            // One batch; on the query paths FilterAsync already pulled these into the session,
            // so this is served from cache rather than costing a request.
            baseDocuments = ids.Count > 0
                ? await session.LoadAsync<object>(ids)
                : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        foreach (var (po, row) in items)
        {
            object? subject = row;
            if (projecting)
            {
                var id = idGetter?.Invoke(row)?.ToString();
                subject = !string.IsNullOrEmpty(id) && baseDocuments!.TryGetValue(id, out var loaded)
                    ? loaded
                    : null;

                if (subject is null)
                {
                    // The rule can't be asked without the document. Unverifiable is not shown.
                    foreach (var attribute in po.Attributes)
                        RedactAttribute(po, attribute.Name);
                    continue;
                }
            }

            var task = (Task)hook!.Invoke(actions, [action, subject])!;
            await task;
            var names = (IReadOnlyCollection<string>?)task.GetCompletedTaskResult();
            if (names is not { Count: > 0 })
                continue;

            foreach (var name in names)
                RedactAttribute(po, name);
        }
    }

    /// <summary>Redact = value gone, attribute invisible — not omitted. A dotted name reaches
    /// into an AsDetail attribute's embedded rows ("Jobs.Salary").</summary>
    private static void RedactAttribute(Abstractions.PersistentObject po, string name)
    {
        var dot = name.IndexOf('.');
        if (dot < 0)
        {
            var attribute = po.Attributes.FirstOrDefault(a => a.Name == name);
            if (attribute is null)
                return;

            attribute.Value = null;
            attribute.Breadcrumb = null;
            attribute.Breadcrumbs = null;
            attribute.IsVisible = false;
            if (attribute is Abstractions.PersistentObjectAttributeAsDetail detail)
            {
                detail.Object = null;
                detail.Objects = detail.Objects is null ? null : [];
            }
            return;
        }

        var parent = po.Attributes.FirstOrDefault(a => a.Name == name[..dot]);
        if (parent is not Abstractions.PersistentObjectAttributeAsDetail asDetail)
            return;

        var childName = name[(dot + 1)..];
        if (asDetail.Object is not null)
            RedactAttribute(asDetail.Object, childName);
        foreach (var child in asDetail.Objects ?? [])
            RedactAttribute(child, childName);
    }

    /// <summary>
    /// The type's rule as one per-request predicate over the base entity, or null when nothing
    /// restricts this caller. Derivation, not interaction: a filter-only type gets its single-row
    /// checks by compiling the expression; a predicate-only type behaves exactly as before; a type
    /// with both must pass both.
    /// </summary>
    private async Task<Func<object, Task<bool>>?> ResolveEffectiveRuleAsync(Type entityType, string action)
    {
        // The system acting — module sync under an mTLS principal, background work with no HTTP
        // request — is not a viewer, and row rules scope viewers. Type-level authorization
        // (security.json Module:* groups) still governs which types it may touch.
        if (Abstractions.Authentication.SparkSystemContext.IsSystemContext(httpContextAccessor))
            return null;

        var hook = ResolveHook(entityType);
        var hookOverridden = IsOverridden(hook);
        var filter = await InvokeGetRowFilterAsync(entityType, action);

        if (!hookOverridden && filter is null)
            return null;

        // Compiled once per (type, action) per request (memoized), because the expression captures
        // request-scoped state and cannot be cached across requests, but re-compiling it for every
        // row / every can-block action within one request is pure waste.
        var compiledFilter = GetCompiledFilter(entityType, action, filter);
        var actions = hookOverridden ? actionsResolver.ResolveForType(entityType) : null;

        return async subject =>
        {
            if (compiledFilter is not null && !(bool)compiledFilter.DynamicInvoke(subject)!)
                return false;

            if (!hookOverridden)
                return true;

            var task = (Task)hook!.Invoke(actions, [action, subject])!;
            await task;
            return (bool)task.GetCompletedTaskResult()!;
        };
    }

    /// <summary>The request's filter expression, or null when the type declares none or the
    /// override returns null for this caller. Construction is async — the hook may await — and
    /// memoized per (type, action) for the request (M2): the underlying hook runs at most once,
    /// and concurrent awaiters share the one <see cref="Task"/>.</summary>
    private Task<LambdaExpression?> InvokeGetRowFilterAsync(Type entityType, string action)
    {
        var key = (entityType, action);
        if (!filterExpressionMemo.TryGetValue(key, out var task))
        {
            task = InvokeGetRowFilterUncachedAsync(entityType, action);
            filterExpressionMemo[key] = task;
        }
        return task;
    }

    private async Task<LambdaExpression?> InvokeGetRowFilterUncachedAsync(Type entityType, string action)
    {
        var method = ResolveFilterHook(entityType);
        if (!IsOverridden(method))
            return null;

        var actions = actionsResolver.ResolveForType(entityType);
        var task = (Task)method!.Invoke(actions, [action])!;
        await task;
        return (LambdaExpression?)task.GetCompletedTaskResult();
    }

    /// <summary>The compiled filter delegate for this (type, action), memoized for the request so a
    /// detail read's Read/Edit/Delete checks and every row of a page reuse one compile.</summary>
    private Delegate? GetCompiledFilter(Type entityType, string action, LambdaExpression? filter)
    {
        if (filter is null)
            return null;

        var key = (entityType, action);
        if (!compiledFilterMemo.TryGetValue(key, out var compiled))
        {
            compiled = filter.Compile();
            compiledFilterMemo[key] = compiled;
        }
        return compiled;
    }

    private static bool IsOverridden(MethodInfo? method)
    {
        if (method is null)
            return false;

        // Declared on the library's base class means nobody overrode it, so the answer is always
        // "allowed" and there is nothing to enforce.
        var declaring = method.DeclaringType;
        return declaring is null
            || !declaring.IsGenericType
            || declaring.GetGenericTypeDefinition() != typeof(DefaultPersistentObjectActions<>);
    }

    private MethodInfo? ResolveHook(Type entityType)
    {
        var actionsType = actionsResolver.ResolveForType(entityType).GetType();
        return ReflectionCache.GetOrAdd<(string Op, Type Actions, Type Entity), MethodInfo?>(
            ("RowSecurity.IsAllowedAsync", actionsType, entityType),
            static k => k.Actions.GetMethod("IsAllowedAsync", [typeof(string), k.Entity]));
    }

    private MethodInfo? ResolveFilterHook(Type entityType)
    {
        var actionsType = actionsResolver.ResolveForType(entityType).GetType();
        return ReflectionCache.GetOrAdd<(string Op, Type Actions, Type Entity), MethodInfo?>(
            ("RowSecurity.GetRowFilterAsync", actionsType, entityType),
            static k => k.Actions.GetMethod("GetRowFilterAsync", [typeof(string)]));
    }

    private MethodInfo? ResolveProtectedHook(Type entityType)
    {
        var actionsType = actionsResolver.ResolveForType(entityType).GetType();
        return ReflectionCache.GetOrAdd<(string Op, Type Actions, Type Entity), MethodInfo?>(
            ("RowSecurity.GetProtectedAttributesAsync", actionsType, entityType),
            static k => k.Actions.GetMethod("GetProtectedAttributesAsync", [typeof(string), k.Entity]));
    }
}
