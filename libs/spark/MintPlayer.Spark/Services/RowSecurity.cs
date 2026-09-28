using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Actions;
using Raven.Client.Documents.Session;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

using static MintPlayer.Spark.Services.SparkHookInvocation;

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
    /// Whether the caller may perform <paramref name="action"/> on every one of
    /// <paramref name="ids"/> — where <paramref name="action"/> is a CUSTOM action's name, not
    /// one of the built-in verbs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This closes a gap that made <c>ISparkRowRule&lt;T&gt;.IsAllowedAsync</c>'s action parameter
    /// a promise the framework never kept: the detail path passes "Read"/"Edit"/"Delete", but no
    /// custom action's name ever reached a row rule, so a rule could not say "may Archive rows
    /// they own, but not rows they can merely see". Every such policy had to be hand-written
    /// inside each action, with nothing to tell the author it was needed.
    /// </para>
    /// <para>
    /// <b>A refinement, never an entry point.</b> It resolves entities directly and therefore
    /// applies NEITHER the type-level right NOR the collection guard. Call it only for ids that
    /// have just been loaded through the row-gated read path, which applied both — otherwise a
    /// caller-supplied id gets judged by a rule that was never told the id is foreign.
    /// </para>
    /// <para>
    /// Costs no request: the read that named these ids left them tracked, so this is an
    /// identity-map hit. It loads under the DECLARED entity type rather than <c>object</c>,
    /// because an untyped load returns a JObject whenever <c>@Raven-Clr-Type</c> does not resolve
    /// and the reflective hook would then reject its own argument.
    /// </para>
    /// <para>
    /// Batched rather than per-id so it cannot regress into an N+1 if the identity map is ever
    /// cold, and all-or-nothing: an id whose document cannot be resolved is refused, matching
    /// <c>FilterAsync</c>'s stance that unverifiable is not shown.
    /// </para>
    /// </remarks>
    Task<bool> AreAllowedAsync(IAsyncDocumentSession session, Type entityType, string action, IReadOnlyCollection<string> ids);

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
    /// Which kinds of row rule govern this entity type — its Actions class and the row policies that
    /// apply to it (#460). <see cref="HasRowRule"/> is "any of them"; the callers that must tell them
    /// apart (spike S5) ask here: the <c>SparkQueryPage&lt;T&gt;</c> refusal ignores filter policies,
    /// and the anonymous-readable validator counts only visibility decisions.
    /// </summary>
    RowRuleKinds GetRowRuleKinds(Type entityType)
        => HasRowRule(entityType) ? RowRuleKinds.ActionsRule : RowRuleKinds.None;

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
        string action,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Pushes the type's row filter into the query itself, when there is one and the shapes allow
    /// it — <paramref name="elementType"/> must be the entity type the filter is written against.
    /// When the query yields an index projection instead, this is a no-op with a diagnostic and
    /// <see cref="FilterAsync"/> remains the enforcement point (batched reload) — the pushdown is
    /// an optimization, never the only gate.
    /// </summary>
    Task<RowFilterComposition> ComposeRowFilterAsync(object queryable, Type entityType, Type elementType, string action, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops the per-request filter memo (#239 M3). Called on a streaming connection's periodic
    /// re-authorization tick: a scoped memo on a socket would otherwise freeze the row filter for
    /// the whole connection, so a caller whose allow-list shrinks would keep seeing revoked rows
    /// until they disconnect — a liveness bug that is also a security bug. Clearing on the same tick
    /// the type-level re-check already runs bounds staleness to that interval. No-op off a stream.
    /// </summary>
    void ResetRequestFilterCache();

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
        string action,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The type's filter expression for this caller and action, straight from the hook, or null when
    /// the type declares none or the override returns null for this caller.
    /// <para>
    /// Exposed so <c>ISparkRowRule&lt;T&gt;</c> can hand an application the raw predicate <em>through
    /// the same per-request memo</em> the pipeline uses. A public service with its own memo would
    /// double hook invocations on any request that touched both a controller and /spark, against a
    /// cap of 30 requests per session — the cap the memo exists to protect (#239).
    /// </para>
    /// </summary>
    Task<LambdaExpression?> GetFilterExpressionAsync(Type entityType, string action);
}

/// <summary>The kinds of row rule that govern an entity type (#460, spike S5).</summary>
[Flags]
internal enum RowRuleKinds
{
    None = 0,

    /// <summary>The Actions class overrides <c>GetRowFilterAsync</c> or <c>IsAllowedAsync</c>.</summary>
    ActionsRule = 1,

    /// <summary>At least one <see cref="IRowFilterPolicy"/> applies.</summary>
    FilterPolicy = 2,

    /// <summary>At least one <see cref="IRowCheckPolicy"/> applies — which refines per row.</summary>
    CheckPolicy = 4,

    /// <summary>At least one applicable policy declares <see cref="IRowPolicy.IsVisibilityDecision"/>.</summary>
    VisibilityPolicy = 8,
}

/// <summary>The questions callers ask of <see cref="RowRuleKinds"/> (#460, spike S5), named once so
/// the call sites and their tests cannot drift apart.</summary>
internal static class RowRuleKindsExtensions
{
    /// <summary>
    /// Whether something narrows <em>who</em> sees which rows — what the startup validator requires
    /// of an anonymously readable type. A non-visibility policy (soft deletion) hides rows from
    /// everyone and must not silently satisfy it.
    /// </summary>
    public static bool DecidesVisibility(this RowRuleKinds kinds)
        => (kinds & (RowRuleKinds.ActionsRule | RowRuleKinds.VisibilityPolicy)) != 0;

    /// <summary>
    /// Whether rows the caller may not see can be removed from an author-paged
    /// <c>SparkQueryPage&lt;T&gt;</c> after the author counted them — which makes its total an
    /// oracle, so the combination is refused. A non-visibility filter policy does not count: what it
    /// hides is hidden from everyone, so the only thing a total could reveal is how many rows are, for
    /// example, soft-deleted.
    /// </summary>
    public static bool RefusesAuthorPagedTotals(this RowRuleKinds kinds)
        => (kinds & (RowRuleKinds.ActionsRule | RowRuleKinds.CheckPolicy | RowRuleKinds.VisibilityPolicy)) != 0;
}

[Register(typeof(IRowSecurity), ServiceLifetime.Scoped)]
internal partial class RowSecurity : IRowSecurity
{
    [Inject] private readonly IActionsResolver actionsResolver;
    [Inject] private readonly ILogger<RowSecurity>? logger;
    [Inject] private readonly Microsoft.AspNetCore.Http.IHttpContextAccessor? httpContextAccessor;
    // Row policies (#460): registered scoped by AddSparkRowPolicy<T>(), composed here and nowhere
    // else, so every path that already asks row security applies them without knowing they exist.
    [Inject] private readonly IEnumerable<IRowPolicy>? rowPolicies;
    [Inject] private readonly IRowPolicyRequestState? requestState;

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

    // Diagnostic (#239 M5): real hook invocations this request (post-memo, so once per distinct
    // (type, action)). The memo bounds today's call graph; this counter warns if a future change
    // reintroduces an N+1-shaped loop that invokes the hook far more than the model should need.
    private int hookInvocations;
    private const int HookInvocationWarnThreshold = 20;

    public Task<LambdaExpression?> GetFilterExpressionAsync(Type entityType, string action)
        => InvokeGetRowFilterAsync(entityType, action);

    public async Task<bool> IsAllowedAsync(Type entityType, string action, object entity)
    {
        var rule = await ResolveEffectiveRuleAsync(entityType, action);
        return rule is null || await rule(entity);
    }

    public async Task<bool> AreAllowedAsync(IAsyncDocumentSession session, Type entityType, string action, IReadOnlyCollection<string> ids)
    {
        if (ids.Count == 0) return true;

        var rule = await ResolveEffectiveRuleAsync(entityType, action);
        // No rule for this action means no restriction — the same "null is unrestricted"
        // convention GetRowFilterAsync uses. A type with no row rule at all is unaffected by
        // this gate entirely.
        if (rule is null) return true;

        var documents = await LoadBaseDocumentsAsync(session, entityType, ids);
        foreach (var id in ids)
        {
            if (!documents.TryGetValue(id, out var entity)) return false;
            if (!await rule(entity)) return false;
        }

        return true;
    }

    public bool HasRowRule(Type entityType) => GetRowRuleKinds(entityType) != RowRuleKinds.None;

    public RowRuleKinds GetRowRuleKinds(Type entityType)
    {
        var kinds = IsOverridden(ResolveHook(entityType)) || IsOverridden(ResolveFilterHook(entityType))
            ? RowRuleKinds.ActionsRule
            : RowRuleKinds.None;

        foreach (var policy in ApplicablePolicies(entityType))
        {
            if (policy is IRowFilterPolicy) kinds |= RowRuleKinds.FilterPolicy;
            if (policy is IRowCheckPolicy) kinds |= RowRuleKinds.CheckPolicy;
            if (policy.IsVisibilityDecision) kinds |= RowRuleKinds.VisibilityPolicy;
        }

        return kinds;
    }

    /// <summary>
    /// The registered policies that govern <paramref name="entityType"/>. <see cref="IRowPolicy.AppliesTo"/>
    /// is fixed per type by contract, so its answer is cached process-wide per (policy type, entity
    /// type) — the per-request cost is a dictionary lookup per registered policy.
    /// </summary>
    private IEnumerable<IRowPolicy> ApplicablePolicies(Type entityType)
    {
        if (rowPolicies is null)
            yield break;

        foreach (var policy in rowPolicies)
        {
            var applies = ReflectionCache.GetOrAdd<(string Op, Type Policy, Type Entity), bool>(
                ("RowSecurity.PolicyAppliesTo", policy.GetType(), entityType),
                k => policy.AppliesTo(k.Entity));
            if (applies)
                yield return policy;
        }
    }

    /// <summary>The policies that take part for this caller: all applicable ones for a viewer, only
    /// those that do not bypass the system context for the system.</summary>
    private IEnumerable<IRowPolicy> ParticipatingPolicies(Type entityType, bool systemContext)
        => ApplicablePolicies(entityType).Where(p => !systemContext || !p.BypassInSystemContext);

    private bool IsSystemContext => Abstractions.Authentication.SparkSystemContext.IsSystemContext(httpContextAccessor);

    private RowPolicyContext CreatePolicyContext(Type entityType, string action, bool systemContext) => new()
    {
        EntityType = entityType,
        Action = action,
        Deleted = requestState?.Deleted ?? SparkDeletedFilter.Exclude,
        IsSystemContext = systemContext,
        User = httpContextAccessor?.HttpContext?.User,
    };

    public void ResetRequestFilterCache()
    {
        filterExpressionMemo.Clear();
        compiledFilterMemo.Clear();
        hookInvocations = 0;   // the diagnostic budget is per-tick on a stream, not per-connection
    }

    public async Task<IReadOnlyList<object>> FilterAsync(
        IAsyncDocumentSession session,
        IReadOnlyList<object> entities,
        Type entityType,
        Type resultType,
        string action,
        CancellationToken cancellationToken = default)
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

        Dictionary<string, object>? baseDocuments = null;
        if (projecting)
        {
            var ids = entities
                .Select(e => idGetter!(e)?.ToString())
                .Where(id => !string.IsNullOrEmpty(id))
                .Cast<string>()
                .ToList();

            baseDocuments = await LoadBaseDocumentsAsync(session, entityType, ids, cancellationToken);
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

    public async Task<RowFilterComposition> ComposeRowFilterAsync(object queryable, Type entityType, Type elementType, string action, CancellationToken cancellationToken = default)
    {
        // Whether IsAllowedAsync refines per row AFTER materialization. Carried on every branch
        // because it decides paging safety independently of whether the expression composed (#431).
        var systemContext = IsSystemContext;

        // Whether anything refines per row AFTER materialization: the Actions class's IsAllowedAsync
        // (never for the system) or any participating check policy (#460).
        var refined = (!systemContext && IsOverridden(ResolveHook(entityType)))
            || ParticipatingPolicies(entityType, systemContext).Any(p => p is IRowCheckPolicy);

        // Same exemption as ResolveEffectiveRuleAsync: the system is not a viewer to scope rows for —
        // unless a policy opted out of the exemption (soft deletion hides rows from background work too).
        if (systemContext && !ParticipatingPolicies(entityType, systemContext).Any())
            return new RowFilterComposition(queryable, RowFilterMode.SystemContext, refined);

        var filter = await InvokeGetRowFilterAsync(entityType, action);
        if (filter is null)
            return new RowFilterComposition(queryable, RowFilterMode.NoRule, refined);

        // A constant predicate ("x => false" for a caller who may see nothing) needs no query
        // translation — and RavenDB's provider may not survive one. The compiled post-filter in
        // FilterAsync evaluates it in memory instead.
        if (filter.Body is ConstantExpression)
            return new RowFilterComposition(queryable, RowFilterMode.ConstantPredicate, refined);

        if (elementType != entityType)
        {
            // #285 (D2): the filter is written over the entity, but the query yields an index
            // projection. Rebind it onto the projection by member name; when every member it reads
            // is on the projection with the same type, it pushes down (spike S2: RavenDB filters the
            // index terms, paging and totals stay correct). FilterAsync still reloads the base
            // documents and re-judges them afterwards — the pushdown narrows what is read, it is
            // never the only gate.
            var rebound = RowFilterExpressions.Rebind(filter, elementType);
            if (rebound is not null)
            {
                if (announced.TryAdd((entityType, $"projection-pushdown:{elementType.Name}"), true))
                {
                    logger?.LogInformation(
                        "Row filter for {EntityType} composes into projection {ProjectionType} by member name.",
                        entityType.Name, elementType.Name);
                }
                return new RowFilterComposition(
                    ApplyWhere(queryable, elementType, rebound), RowFilterMode.PushedDownOntoProjection, refined);
            }

            // A member the filter reads is not on the projection. FilterAsync stays the gate (batched
            // base-document reload) — filtered, just not pushed down. Announced once so an
            // O(collection) query on a large type is visible.
            if (announced.TryAdd((entityType, $"fallback:{elementType.Name}"), true))
            {
                logger?.LogInformation(
                    "Row filter for {EntityType} cannot compose into projection {ProjectionType} (a member it "
                    + "reads is not on the projection); falling back to post-materialization filtering with a "
                    + "batched reload.",
                    entityType.Name, elementType.Name);
            }
            return new RowFilterComposition(queryable, RowFilterMode.ProjectionFallback, refined);
        }

        if (announced.TryAdd((entityType, "pushdown"), true))
        {
            logger?.LogInformation(
                "Row security for {EntityType}: filter expression composes into the database query{Refinement}.",
                entityType.Name,
                refined ? " with a per-row refinement (IsAllowedAsync or a check policy)" : "");
        }

        return new RowFilterComposition(ApplyWhere(queryable, entityType, filter), RowFilterMode.PushedDown, refined);
    }

    private static object ApplyWhere(object queryable, Type elementType, LambdaExpression predicate)
    {
        var whereMethod = ReflectionCache.GetOrAdd<(string Op, Type Entity), MethodInfo>(
            ("RowSecurity.QueryableWhere", elementType),
            static k => typeof(Queryable).GetMethods()
                .First(m => m.Name == nameof(Queryable.Where)
                    && m.GetParameters().Length == 2
                    // Expression<Func<T,bool>>, not the indexed Expression<Func<T,int,bool>> overload.
                    && m.GetParameters()[1].ParameterType.GetGenericArguments()[0].GetGenericArguments().Length == 2)
                .MakeGenericMethod(k.Entity));

        return whereMethod.Invoke(null, [queryable, predicate])!;
    }

    public async Task RedactAsync(
        IAsyncDocumentSession session,
        IReadOnlyList<(Abstractions.PersistentObject Po, object Row)> items,
        Type entityType,
        Type resultType,
        string action,
        CancellationToken cancellationToken = default)
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

            // On the query paths FilterAsync already pulled these into the session, so this is
            // served from the identity map rather than costing a request.
            baseDocuments = await LoadBaseDocumentsAsync(session, entityType, ids, cancellationToken);
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

            var task = (Task)hook!.Invoke(actions, HookInvoke, binder: null, parameters: [action, subject], culture: null)!;
            await task;
            var names = (IReadOnlyCollection<string>?)task.GetCompletedTaskResult();
            if (names is not { Count: > 0 })
                continue;

            foreach (var name in names)
                RedactAttribute(po, name);
        }
    }

    /// <summary>
    /// Batch-loads the documents behind a page of projected rows, as <paramref name="entityType"/>.
    /// <para>
    /// The type argument is load-bearing, not cosmetic. Both row hooks are declared over the entity
    /// (<c>Expression&lt;Func&lt;TEntity, bool&gt;&gt;</c>, <c>GetProtectedAttributesAsync(string,
    /// TEntity)</c>) and both are invoked reflectively, so a value RavenDB materialized as something
    /// else fails the argument check before a single row is judged. Asking for <c>object</c> made
    /// that outcome depend on the stored document: RavenDB recovers the CLR type from
    /// <c>@Raven-Clr-Type</c> when it can, and falls back to a <c>JObject</c> when the metadata is
    /// absent or names a type this process cannot resolve — a raw put, a bulk insert, an import, an
    /// entity since moved between assemblies. Naming the declared type here removes that dependency
    /// rather than narrowing it (#281).
    /// </para>
    /// <para>
    /// It also primes the session's identity map with correctly-typed instances. The map returns a
    /// tracked entity regardless of what type a later load asks for, so the untyped loads that run
    /// after this one within a request — redaction, breadcrumb resolution — reuse these rather than
    /// re-deriving a type from metadata that may not resolve.
    /// </para>
    /// <para>
    /// One batched request, never one per row: the request session caps requests
    /// (MaxNumberOfRequestsPerSession, default 30), so a per-row load would throw past ~29 rows.
    /// Ids with no document are simply absent — callers treat unverifiable as not shown.
    /// </para>
    /// </summary>
    internal static async Task<Dictionary<string, object>> LoadBaseDocumentsAsync(
        IAsyncDocumentSession session, Type entityType, IReadOnlyCollection<string> ids,
        CancellationToken cancellationToken = default)
    {
        // Same comparer RavenDB builds its own result with: document ids are case-insensitive, and
        // an index-projected Id can differ in case from the stored one.
        var baseDocuments = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (ids.Count == 0)
            return baseDocuments;

        var loadMethod = ReflectionCache.GetOrAdd<(string Op, Type Entity), MethodInfo?>(
            ("RowSecurity.SessionLoadManyAsync", entityType),
            static k => typeof(IAsyncDocumentSession)
                .GetMethod(nameof(IAsyncDocumentSession.LoadAsync), [typeof(IEnumerable<string>), typeof(CancellationToken)])
                ?.MakeGenericMethod(k.Entity));

        // Reflection applies no default arguments, so the token is passed explicitly.
        if (loadMethod?.Invoke(session, [ids, cancellationToken]) is not Task task)
            return baseDocuments;

        await task;

        // Task<Dictionary<string, TEntity>>, copied into the object-valued shape the callers hold.
        if (task.GetCompletedTaskResult() is System.Collections.IDictionary loaded)
        {
            foreach (System.Collections.DictionaryEntry entry in loaded)
            {
                if (entry.Value is not null)
                    baseDocuments[(string)entry.Key] = entry.Value;
            }
        }

        return baseDocuments;
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
        // A policy may opt out of that exemption (BypassInSystemContext = false); only those remain.
        var systemContext = IsSystemContext;
        if (systemContext && !ParticipatingPolicies(entityType, systemContext).Any())
            return null;

        var hook = ResolveHook(entityType);
        var hookOverridden = !systemContext && IsOverridden(hook);
        var filter = await InvokeGetRowFilterAsync(entityType, action);
        var checks = ParticipatingPolicies(entityType, systemContext).OfType<IRowCheckPolicy>().ToList();

        if (!hookOverridden && filter is null && checks.Count == 0)
            return null;

        // Compiled once per (type, action) per request (memoized), because the expression captures
        // request-scoped state and cannot be cached across requests, but re-compiling it for every
        // row / every can-block action within one request is pure waste.
        var compiledFilter = GetCompiledFilter(entityType, action, filter);
        var actions = hookOverridden ? actionsResolver.ResolveForType(entityType) : null;
        var context = checks.Count > 0 ? CreatePolicyContext(entityType, action, systemContext) : null;

        // Per-row rule = compiled filter (actions filter AND every filter policy) AND the actions
        // class's IsAllowedAsync AND every check policy — cheapest first, first refusal wins.
        return async subject =>
        {
            if (compiledFilter is not null && !(bool)compiledFilter.DynamicInvoke(subject)!)
                return false;

            if (hookOverridden)
            {
                var task = (Task)hook!.Invoke(actions, HookInvoke, binder: null, parameters: [ActionsHookVerb(action), subject], culture: null)!;
                await task;
                if (!(bool)task.GetCompletedTaskResult()!)
                    return false;
            }

            foreach (var check in checks)
            {
                if (!await check.IsAllowedAsync(context!, subject))
                    return false;
            }

            return true;
        };
    }

    /// <summary>
    /// The verb an Actions class's own row hooks (<c>GetRowFilterAsync</c>, <c>IsAllowedAsync</c>)
    /// are asked about. <c>Restore</c> and <c>Revert</c> are edits of the stored row and <c>Purge</c>
    /// a delete of it (#460, M7), so they reach those hooks as <c>Edit</c> / <c>Delete</c>: a rule
    /// written for the built-in verbs — "only the owner may edit" — then governs them too, instead of
    /// letting an unfamiliar name fall through to "unrestricted". Row policies still see the real
    /// name (<see cref="RowPolicyContext.Action"/>), which is how SoftDelete confines a restore to a
    /// deleted row while <c>Edit</c> keeps hiding it.
    /// </summary>
    internal static string ActionsHookVerb(string action) => action switch
    {
        "Restore" or "Revert" => "Edit",
        "Purge" => "Delete",
        _ => action,
    };

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

    /// <summary>
    /// The effective filter for (type, action): the Actions class's <c>GetRowFilterAsync</c> AND every
    /// participating <see cref="IRowFilterPolicy"/>, joined by parameter rebinding and <c>AndAlso</c>
    /// (never <c>Expression.Invoke</c>, which RavenDB does not translate), constant-folded. Spike S1
    /// measured the result on both engines: <c>((actions and policy)) and (column) and (search or
    /// search)</c> — the security predicate stays one AND group ahead of the search group.
    /// </summary>
    private async Task<LambdaExpression?> InvokeGetRowFilterUncachedAsync(Type entityType, string action)
    {
        var systemContext = IsSystemContext;
        var parts = new List<(LambdaExpression Filter, string Source)>();

        // The Actions class's own filter scopes viewers; the system is exempt from it.
        var method = ResolveFilterHook(entityType);
        if (!systemContext && IsOverridden(method))
        {
            CountHookInvocation();
            var actions = actionsResolver.ResolveForType(entityType);
            var task = (Task)method!.Invoke(actions, HookInvoke, binder: null, parameters: [ActionsHookVerb(action)], culture: null)!;
            await task;
            if ((LambdaExpression?)task.GetCompletedTaskResult() is { } actionsFilter)
                parts.Add((actionsFilter, $"{actions.GetType().Name}.GetRowFilterAsync"));
        }

        RowPolicyContext? context = null;
        var policyContributed = false;
        foreach (var policy in ParticipatingPolicies(entityType, systemContext).OfType<IRowFilterPolicy>())
        {
            CountHookInvocation();
            context ??= CreatePolicyContext(entityType, action, systemContext);
            if (await policy.GetFilterAsync(context) is { } policyFilter)
            {
                parts.Add((policyFilter, policy.GetType().Name));
                policyContributed = true;
            }
        }

        return parts.Count switch
        {
            0 => null,
            // Only the Actions class's filter: returned as written, so a type no policy governs keeps
            // exactly the expression — and the constant-predicate handling — it had before policies.
            1 when !policyContributed => parts[0].Filter,
            _ => RowFilterExpressions.Combine(entityType, parts),
        };
    }

    /// <summary>
    /// Diagnostic (#239 M5, #460): counts actions-hook and policy invocations alike, post-memo. With
    /// the memo they are bounded by distinct (type, action) × (1 + policies); the warning catches a
    /// future loop that reaches them per row.
    /// </summary>
    private void CountHookInvocation()
    {
        if (++hookInvocations == HookInvocationWarnThreshold)
        {
            logger?.LogWarning(
                "Row-filter hook invoked {Count} times in one request/tick — more than the model "
                + "should need. Suspect an N+1 (a per-row or per-document loop reaching the hook). "
                + "Types touched: {Types}.",
                HookInvocationWarnThreshold,
                string.Join(", ", filterExpressionMemo.Keys.Select(k => k.EntityType.Name).Distinct()));
        }
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
