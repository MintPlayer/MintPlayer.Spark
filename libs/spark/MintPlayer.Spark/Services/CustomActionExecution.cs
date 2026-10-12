using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Exceptions;

namespace MintPlayer.Spark.Services;

/// <summary>What a custom action is asked to run on: the wire shape of <c>POST /spark/actions/execute</c>, minus the type.</summary>
/// <param name="ActionName">The custom action, by name.</param>
/// <param name="Parent">The route type's own object (detail-page invocation), by its submitted id; loaded under the action's type (C3).</param>
/// <param name="SelectedItemIds">The ids of the selected rows; re-materialized through <paramref name="QueryId"/>.</param>
/// <param name="ParentId">A sub-query's container, by id; resolved under <paramref name="ParentType"/>.</param>
/// <param name="ParentType">The container's type.</param>
/// <param name="QueryId">The query the selection was ticked in.</param>
/// <param name="Retry">The request whose retry answers apply, or <see langword="null"/> for a first attempt.</param>
internal sealed record CustomActionInvocation(
    string ActionName,
    PersistentObject? Parent,
    IReadOnlyList<string>? SelectedItemIds,
    string? ParentId,
    string? ParentType,
    string? QueryId,
    IRetryableRequest? Retry = null);

/// <summary>How a custom action invocation ended, short of an exception.</summary>
internal enum CustomActionOutcomeKind
{
    /// <summary>The action ran; <see cref="CustomActionOutcome.Result"/> is what it handed to <c>SetResult</c>.</summary>
    Executed,
    /// <summary>Refused as a not-found is refused (M-3): the type grant, a row gate, or a missing row.</summary>
    Refused,
    /// <summary>The action is not in the catalogue, or no class implements it.</summary>
    NotFound,
    /// <summary>The input breaks a rule that is not authorization (selection size, selection rule, missing query).</summary>
    Invalid,
    /// <summary>The action is disabled on one of its targets (#460, D13).</summary>
    Disabled,
}

/// <summary>The outcome of <see cref="ICustomActionExecution.ExecuteAsync"/>.</summary>
internal sealed record CustomActionOutcome(CustomActionOutcomeKind Kind, object? Result = null, string? Error = null)
{
    public static readonly CustomActionOutcome Refused = new(CustomActionOutcomeKind.Refused);
}

/// <summary>
/// The whole gate sequence of <c>POST /spark/actions/execute</c>, in one place both the HTTP endpoint and
/// the MCP tools call (MCP PRD D9): the type grant, the catalogue gate, the selection cap and rule, the
/// row-gated re-materialization, the per-row action check, the disabled-action gate and the last gates
/// (quotas), then the action itself.
/// </summary>
/// <remarks>
/// <see cref="ICustomActionResolver"/> alone resolves an action class and checks nothing; every caller
/// that runs an action on a user's behalf goes through here. Exceptions the action raises
/// (<see cref="SparkAccessDeniedException"/>, <see cref="SparkActionDisabledException"/>,
/// <see cref="SparkValidationException"/>, <see cref="SparkThrottledException"/>, a retry) propagate to
/// the caller, which maps them.
/// </remarks>
internal interface ICustomActionExecution
{
    Task<CustomActionOutcome> ExecuteAsync(EntityTypeDefinition entityType, CustomActionInvocation invocation, CancellationToken cancellationToken = default);
}

[Register(typeof(ICustomActionExecution), ServiceLifetime.Scoped)]
internal sealed partial class CustomActionExecution : ICustomActionExecution
{
    /// <summary>
    /// Upper bound on submitted selected items, whatever the action's selection rule says.
    /// </summary>
    /// <remarks>
    /// Deliberately generous — real selections are single- or double-digit — while still
    /// bounding what one request can cost. See the comment at the check for why the existing
    /// "estimatedRequests" figure is not a bound at all.
    /// </remarks>
    private const int MaxSelectedItems = SparkDefaultActions.MaxSelectedItems;

    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IRowSecurity rowSecurity;
    [Inject] private readonly ISparkTypeResolver typeResolver;
    [Inject] private readonly ICustomActionResolver actionResolver;
    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly IRetryAccessor retryAccessor;
    [Inject] private readonly ILogger<CustomActionExecution> logger;
    [Inject] private readonly IDatabaseAccess databaseAccess;
    // The request-scoped session — the same instance IDatabaseAccess uses, so an IgnoreMaxRequests
    // scope opened here covers the row-gated loads below.
    [Inject] private readonly Raven.Client.Documents.Session.IAsyncDocumentSession session;
    [Inject] private readonly IActionsCatalogueLoader catalogueLoader;
    [Inject] private readonly IQueryLoader queryLoader;
    [Inject] private readonly ISparkSelectionResolver selectionResolver;
    [Inject] private readonly IDisabledActionsEvaluator disabledActions;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;
    // Optional so the dispatch tests that construct this service by hand keep compiling; DI always
    // supplies it.
    // X3: the last gates before an action runs (per-user quotas). Optional for the same reason.
    [Inject] private readonly IEnumerable<ISparkCustomActionGate>? gates;

    public async Task<CustomActionOutcome> ExecuteAsync(EntityTypeDefinition entityType, CustomActionInvocation invocation, CancellationToken cancellationToken = default)
    {
        var actionName = invocation.ActionName;
        var typeName = entityType.Name;

        try
        {
            await permissionService.EnsureAuthorizedAsync(actionName, typeName);
        }
        catch (SparkAccessDeniedException)
        {
            return CustomActionOutcome.Refused;
        }

        // Security sweep M3: execution must agree with the listing. The action resolver scans every
        // ICustomAction in the AppDomain, so an action shipped by a referenced library — or one
        // retired by removing it from actions.json (the documented way) — was still callable
        // by name. Gate on the catalogue, exactly as ListCustomActions does: absent → 404.
        // New, Edit and Delete are the framework's own (#460 D18, #467 D7): they run through
        // /po/new, the edit page and /po/delete-many — never here, whatever ICustomAction class
        // happens to share the name.
        var definition = SparkDefaultActions.IsDefault(actionName) ? null : catalogueLoader.GetCatalogue().Find(actionName);
        if (definition is null)
        {
            return new(CustomActionOutcomeKind.NotFound, Error: $"Custom action '{actionName}' not found");
        }

        var action = actionResolver.Resolve(actionName);
        if (action is null)
        {
            return new(CustomActionOutcomeKind.NotFound, Error: $"Custom action '{actionName}' not found");
        }

        var selectedCount = invocation.SelectedItemIds?.Count ?? 0;

        // A hard ceiling on the selection, whether or not a rule is declared.
        //
        // This is NOT belt-and-braces for the rule below. IgnoreMaxRequests sets
        // MaxNumberOfRequestsPerSession to int.MaxValue for the whole handler, so RavenDB's own
        // cap is not a backstop here.
        //
        // Since #327 M2 the selection costs ONE batched load rather than a round-trip per id, so
        // this ceiling no longer bounds round-trips. It still bounds work: the batch materializes
        // every named document at once, and each row then costs a collection-guard check, a
        // row-rule evaluation, mapping and redaction. Without it, any caller holding one action
        // grant can turn a single request into an unbounded multi-document read, and no rate
        // limiter is on this route by default.
        if (selectedCount > MaxSelectedItems)
        {
            return new(CustomActionOutcomeKind.Invalid,
                Error: $"At most {MaxSelectedItems} items can be selected; {selectedCount} were submitted.");
        }

        // Enforce the declared selection rule, BEFORE the reload loop below, so a violating
        // request costs no database work.
        //
        // Scoped to the query path — "the request named no parent" — because the rule
        // describes a query view's selection. Fleet's CarCopy is "=1" with showedOn "both",
        // and its detail-page invocation legitimately sends a parent and no selection;
        // enforcing there would 400 the very action this rule was written for.
        //
        // ⚠️ This is input validation, not authorization. The gate is the grant checked
        // above, which holds regardless of which query the caller clicked from — a caller
        // can always POST directly, and no narrowing here changes that.
        var invokedFromQuery = invocation.Parent is null || string.IsNullOrEmpty(invocation.Parent.Id);
        if (invokedFromQuery && !SelectionRuleParser.Parse(definition.SelectionRule)(selectedCount))
        {
            return new(CustomActionOutcomeKind.Invalid,
                Error: $"Action '{actionName}' requires a selection of '{definition.SelectionRule}'; {selectedCount} items were submitted.");
        }

        // D12 (#467): a selection is resolved through the query it was ticked in, so that query's
        // OnDisableActionsAsync decision always applies. Omitting the query used to skip it.
        if (selectedCount > 0 && string.IsNullOrEmpty(invocation.QueryId))
        {
            return new(CustomActionOutcomeKind.Invalid,
                Error: $"Action '{actionName}' on a selection must name the query its rows were selected in (queryId).");
        }

        RetryScope.Accept(retryAccessor, invocation.Retry);

        // The selection is resolved in ONE batched pass (#327 M2), so the request cost is
        // O(breadcrumb depth), not O(selected rows). This budget used to be sized to the item
        // count — 30 + (1 + N) * 6 — because resolving each row was a full row-gated load of
        // its own; at MaxSelectedItems that was a four-figure round-trip count behind a
        // deliberately lifted ceiling. Lifting the ceiling was documented as the fix; it was
        // the mitigation. The ceiling is still lifted, because a bulk action legitimately needs
        // more than RavenDB's stock 30 (the parent load, the batch, breadcrumb levels, and
        // whatever the action itself does), but it is now a small constant, and an overrun is
        // a signal worth reading rather than an expected consequence of a large selection.
        const int ActionRequestBudget = 30;
        // Named, so the overrun warning identifies itself. It used to report the CallerMemberName
        // — "HandleAsync performed 412 requests" — a number with no subject, from a name several
        // endpoints share.
        using var _ = session.IgnoreMaxRequests(
            ActionRequestBudget, logger,
            $"action '{actionName}' on '{entityType.Name}' ({selectedCount} selected)");

        // Row-gated server-side resolution (#236 G3). The wire's Parent and selected ids are
        // whatever the caller typed — a caller holding the type-level action right could name
        // any id of any type and the action received it as fact. The action now gets entities
        // re-loaded through the same row-gated path as every read; a denied or missing id is
        // a 404, indistinguishable from not-found (M-3). The submitted POs stay available as
        // exactly that — submitted values — for actions that edit.
        //
        // Security sweep C3: the load MUST use the route's entityType.Id, NOT the wire's
        // submittedParent.ObjectTypeId. The type gate above authorized THIS action on THIS
        // type; loading the parent under a client-chosen type would gate it against the wrong
        // rule (and, pre-CollectionGuard, smuggle a foreign-collection id past every row rule).
        // The selection already does this correctly below.
        PersistentObject? parent = null;
        if (invocation.Parent is { } submittedParent && !string.IsNullOrEmpty(submittedParent.Id))
        {
            parent = await databaseAccess.GetPersistentObjectAsync(entityType.Id, submittedParent.Id);
            if (parent is null)
            {
                return CustomActionOutcome.Refused;
            }
        }

        // The sub-query's container, when there is one. Resolved under ITS OWN type, which is
        // the one place that is correct rather than a C3 violation: the container is a
        // different type from the action's by construction (Cars listed on a Company page), so
        // loading it under the route type would hand a Company id to the Car collection guard
        // and refuse every time. Safety comes from GetPersistentObjectAsync applying that
        // type's own Read gate and row rule — a container the caller may not see refuses the
        // request rather than arriving as a fact.
        PersistentObject? queryParent = null;
        string? queryParentTypeName = null;
        if (!string.IsNullOrEmpty(invocation.ParentId) && !string.IsNullOrEmpty(invocation.ParentType))
        {
            var parentTypeDefinition = modelLoader.ResolveEntityType(invocation.ParentType);
            if (parentTypeDefinition is null)
            {
                return CustomActionOutcome.Refused;
            }

            // Live rows only, deliberately (#460). The query reads take a `parentDeleted` mode so a
            // deleted object opened from the recycle bin can list its sub-queries; this path
            // does not, because that page offers no actions — a deleted container is refused here.
            queryParent = await databaseAccess.GetPersistentObjectAsync(parentTypeDefinition.Id, invocation.ParentId);
            if (queryParent is null)
            {
                return CustomActionOutcome.Refused;
            }

            queryParentTypeName = parentTypeDefinition.Name;
        }

        // Selected items come from this type's list screen; an id-less one names no row and
        // cannot be verified, so it fails the whole request rather than being skipped.
        var submittedIds = (invocation.SelectedItemIds ?? []).ToList();
        if (submittedIds.Any(string.IsNullOrEmpty))
        {
            return CustomActionOutcome.Refused;
        }

        // Re-materialize the selection by RE-RUNNING THE QUERY it came from, narrowed to these
        // ids — so the action receives the rows the grid actually had, with the query's own
        // projection. Loading the documents instead would re-derive something adjacent: an
        // index-computed column would come back null, a query bound to a non-default index would
        // yield the wrong shape, and a composed query (no clrType, no documents) could not be
        // materialized at all — it would loop the page-compose hook and hand the action N copies
        // of the page object wearing row ids.
        //
        // Falls back to the row-gated document load for the two shapes that cannot be re-run: a
        // query owning its own paging and a streaming query. A selection naming no query was refused above (D12).
        var selectedItems = submittedIds.Count == 0
            ? []
            : await MaterializeSelectionAsync(invocation.QueryId, entityType, submittedIds, queryParent, cancellationToken);

        if (selectedItems is null)
        {
            return CustomActionOutcome.Refused;
        }

        // Never shrink silently. A row is missing when it names nothing, names a foreign
        // collection, or is refused by the row rule — all indistinguishable on purpose — and
        // acting on the survivors would let a bulk action quietly process 498 of 500 rows.
        //
        // ⚠️ Compared against what the SOURCE yielded, never against the submitted list. Zip the
        // two, or pad the result with id-only stubs, and this check degrades to `n == n`.
        //
        // Distinct because duplicates collapse, and selecting the same row twice is not an error.
        var distinctRequested = submittedIds.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (selectedItems.Count != distinctRequested)
        {
            return CustomActionOutcome.Refused;
        }

        // Ask the row rule about THIS action, not about "Read".
        //
        // The loads above gated every named row on "Read" — necessary (acting on a row you
        // cannot see is a blind write and an existence oracle) but not sufficient: it answers
        // "may I see this", never "may I Archive this". Only rows actually named are checked;
        // a pure command that names none is governed solely by its {ActionName}/{Type} grant,
        // and inventing a synthetic subject for it would either deny every command or teach
        // authors the check is vacuous.
        //
        // All-or-nothing, before ExecuteAsync runs. Filtering would hand the action a quietly
        // smaller set, and with refreshOnCompleted the user would see a refreshed grid and
        // assume all of it happened. Reporting WHICH rows were dropped is itself disclosure,
        // so silent filtering is the only M-3-compatible filtering — and it is worse than a
        // refusal.
        var rowIds = selectedItems.Select(i => i.Id)
            .Concat(parent?.Id is { Length: > 0 } parentId ? [parentId] : Array.Empty<string>())
            .Where(rowId => !string.IsNullOrEmpty(rowId))
            .ToArray();

        // F2. The two reasons clrType can be absent are not the same reason, and used to be
        // conflated into one `is not null` guard that skipped the row gate for both.
        //
        //  - The type DECLARES no clrType: it is composed, its rows are computed rather than
        //    stored, and there is no document for AreAllowedAsync to judge. Proceeding is
        //    correct — the actions class owns row scoping for such a type.
        //  - The type declares one and it does NOT resolve: the class was renamed, or its
        //    assembly is not referenced. That is a broken binding, and skipping the gate means
        //    a custom action runs against rows nobody authorized. Every other path in the
        //    framework throws loudly on exactly this condition; this one used to swallow it.
        Type? clrType = null;
        if (!string.IsNullOrEmpty(entityType.ClrType))
        {
            clrType = typeResolver.Resolve(entityType.ClrType)
                ?? throw new InvalidOperationException(
                    $"Custom action '{actionName}' targets '{entityType.Name}', whose declared clrType " +
                    $"'{entityType.ClrType}' is not declared by any loaded assembly. The per-row " +
                    $"authorization check cannot run without it, and running the action anyway would " +
                    $"execute against rows that were never authorized. Re-run '--spark-synchronize-model' " +
                    $"if the class was renamed, or reference the assembly declaring it.");
        }

        if (rowIds.Length > 0 && clrType is not null &&
            !await rowSecurity.AreAllowedAsync(session, clrType, actionName, rowIds))
        {
            return CustomActionOutcome.Refused;
        }

        // Disabled-action gate (#460, D13) — after every row gate above, so a row the caller may
        // not see is still a 404 and only a visible one can answer 403. The same hook that filled
        // DisabledActions when the page loaded is asked about every target this action touches:
        // the object it runs on, the query it was invoked from, and each selected row. The union
        // decides, because a button hidden on any of them was never offered for this request.
        var disabled = await EvaluateDisabledAsync(
            entityType, clrType, actionName, parent, invocation.QueryId, queryParent, queryParentTypeName, selectedItems);
        if (disabled.Contains(actionName))
        {
            return new(CustomActionOutcomeKind.Disabled);
        }

        var args = new CustomActionArgs
        {
            Parent = parent,
            QueryParent = queryParent,
            QueryParentType = queryParentTypeName,
            SelectedItems = [.. selectedItems],
            SubmittedParent = invocation.Parent,
            SubmittedSelectedItemIds = [.. submittedIds],
        };

        // Last gates (X3, per-user quotas): after every check above, so only a request that would
        // otherwise run is counted, and a 429 discloses nothing a 404 would hide.
        if (gates is not null)
        {
            var httpContext = httpContextAccessor.HttpContext!;
            var gateContext = new SparkCustomActionGateContext
            {
                ActionName = actionName,
                ActionType = action.GetType(),
                EntityTypeName = typeName,
                User = httpContext.User,
                HttpContext = httpContext,
            };
            foreach (var gate in gates)
                await gate.BeforeExecuteAsync(gateContext, cancellationToken);
        }

        await action.ExecuteAsync(args, cancellationToken);

        // A persistent object an action hands back is a presentation like any other (M2c-2a). It is
        // not presented here any more: loaded or scaffolded, it was built for this caller (D13a),
        // and one the action made itself is caught by the boundary net.

        // T5 (#460): whatever the action handed to SetResult, as the envelope's result. Null when
        // it set nothing, which is the shape every existing caller already reads.
        return new(CustomActionOutcomeKind.Executed, Result: args.Result);
    }

    /// <summary>
    /// Asks <c>OnDisableActionsAsync</c> about every target of this submit, in one batched call, and
    /// returns the union of what it withheld (#460, D13).
    /// </summary>
    /// <remarks>
    /// Every target is of the action's own type — the parent is loaded under the route type (C3), the
    /// rows come from this type's query, and the query was checked to produce this type's rows — so
    /// one actions class answers for all of them. The sub-query's container is a different type and
    /// is not a target: it is context on the query target, exactly as it is when the grid loads.
    /// Entities are the <b>stored</b> documents, one batched load that the row gate above has already
    /// put in the session.
    /// </remarks>
    private async Task<IReadOnlySet<string>> EvaluateDisabledAsync(
        EntityTypeDefinition entityType,
        Type? clrType,
        string actionName,
        PersistentObject? parent,
        string? queryId,
        PersistentObject? queryParent,
        string? queryParentTypeName,
        IReadOnlyList<QueryResultItem> selectedItems)
    {
        var actions = disabledActions.ResolveActions(clrType, entityType.Name);
        if (actions is null)
            return new HashSet<string>();

        var ids = selectedItems.Select(i => i.Id)
            .Append(parent?.Id)
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        IReadOnlyDictionary<string, object> entities = clrType is not null && ids.Length > 0
            ? await disabledActions.LoadEntitiesAsync(clrType, ids)
            : new Dictionary<string, object>();

        DisableActionsItem ForObject(string? id) => new(new DisabledActionSet(), new DisableActionsContext
        {
            Phase = DisableActionsPhase.Submit,
            TargetKind = DisableActionsTargetKind.PersistentObject,
            ActionName = actionName,
            Id = id,
            Entity = id is not null && entities.TryGetValue(id, out var entity) ? entity : null,
        });

        var items = new List<DisableActionsItem>(selectedItems.Count + 2);
        if (parent is not null)
            items.Add(ForObject(parent.Id));

        if (!string.IsNullOrEmpty(queryId)
            && queryLoader.ResolveQuery(queryId) is { } query
            && string.Equals(query.EntityType, entityType.Name, StringComparison.OrdinalIgnoreCase))
        {
            items.Add(new DisableActionsItem(new DisabledActionSet(), new DisableActionsContext
            {
                Phase = DisableActionsPhase.Submit,
                TargetKind = DisableActionsTargetKind.Query,
                ActionName = actionName,
                Query = MintPlayer.Spark.Queries.SparkQueryInfo.From(query),
                Parent = queryParent,
                ParentType = queryParentTypeName,
            }));
        }

        foreach (var row in selectedItems)
            items.Add(ForObject(row.Id));

        return await disabledActions.EvaluateAsync(actions, items);
    }

    /// <summary>
    /// The selected rows, re-materialized server-side through the query they were ticked in.
    /// <see langword="null"/> means refuse.
    /// </summary>
    /// <remarks>
    /// Re-running the query narrowed to these ids hands the action the rows the grid actually had,
    /// with the query's own projection (index-computed columns included); a composed query works at
    /// all. <see cref="ISparkSelectionResolver"/> does it, shared with delete-many, and also checks
    /// every row readable (#467, D11). A query that cannot be re-run falls back to the row-gated
    /// document load there.
    /// </remarks>
    private async Task<IReadOnlyList<QueryResultItem>?> MaterializeSelectionAsync(
        string? queryId,
        EntityTypeDefinition entityType,
        IReadOnlyList<string> submittedIds,
        PersistentObject? queryParent,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(queryId) || queryLoader.ResolveQuery(queryId) is not { } query)
            return null;

        return await selectionResolver.ResolveAsync(entityType, query, queryParent, submittedIds, cancellationToken);
    }
}
