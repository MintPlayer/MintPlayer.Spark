using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Actions;
// The sibling namespace MintPlayer.Spark.Endpoints.PersistentObject shadows the type name here.
using Po = MintPlayer.Spark.Abstractions.PersistentObject;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Exceptions;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.Actions;

[MemberOf<ActionsGroup>]
internal sealed partial class ExecuteCustomAction : IPostEndpoint
{
    public static string Path => "/execute";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
    {
        builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));
    }

    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IRowSecurity rowSecurity;
    [Inject] private readonly ISparkTypeResolver typeResolver;

    /// <summary>
    /// Upper bound on submitted selected items, whatever the action's selection rule says.
    /// </summary>
    /// <remarks>
    /// Deliberately generous — real selections are single- or double-digit — while still
    /// bounding what one request can cost. See the comment at the check for why the existing
    /// "estimatedRequests" figure is not a bound at all.
    /// </remarks>
    private const int MaxSelectedItems = SparkDefaultActions.MaxSelectedItems;
    [Inject] private readonly ICustomActionResolver actionResolver;
    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly IRetryAccessor retryAccessor;
    [Inject] private readonly IClientAccessor clientAccessor;
    [Inject] private readonly ILogger<ExecuteCustomAction> logger;
    [Inject] private readonly IDatabaseAccess databaseAccess;
    // The request-scoped session — the same instance IDatabaseAccess uses, so an IgnoreMaxRequests
    // scope opened here covers the row-gated loads below.
    [Inject] private readonly Raven.Client.Documents.Session.IAsyncDocumentSession session;
    [Inject] private readonly IActionsCatalogueLoader catalogueLoader;
    [Inject] private readonly IQueryLoader queryLoader;
    [Inject] private readonly ISparkSelectionResolver selectionResolver;
    [Inject] private readonly IDisabledActionsEvaluator disabledActions;
    // Optional so the dispatch tests that construct this endpoint by hand keep compiling; DI always
    // supplies it.

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        // Both the type and the action name arrive in the body now, so the body is read first. The
        // ordering the old code relied on — authorize, then read — is preserved in effect because a
        // malformed body is refused here in exactly the shape an unknown type is refused below.
        var (request, entityType) = await SparkRequestType.ReadAsync<CustomActionRequest>(httpContext, modelLoader);
        var actionName = request?.ActionName;

        if (request is null || entityType is null || string.IsNullOrEmpty(actionName))
        {
            // Same shape as a denial. This ran BEFORE the grant check below, so a specific
            // 404 here against a 401 there told an anonymous caller which entity types are
            // real -- the M-3 oracle, in the one endpoint the sweep missed.
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
        }

        var typeName = entityType.Name;

        try
        {
            await permissionService.EnsureAuthorizedAsync(actionName, typeName);
        }
        catch (SparkAccessDeniedException)
        {
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
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
            return ClientResult.Envelope(clientAccessor, new { error = $"Custom action '{actionName}' not found" }, StatusCodes.Status404NotFound);
        }

        var action = actionResolver.Resolve(actionName);
        if (action is null)
        {
            return ClientResult.Envelope(clientAccessor, new { error = $"Custom action '{actionName}' not found" }, StatusCodes.Status404NotFound);
        }

        var selectedCount = request.SelectedItemIds?.Length ?? 0;

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
            return ClientResult.Envelope(clientAccessor,
                new { error = $"At most {MaxSelectedItems} items can be selected; {selectedCount} were submitted." },
                StatusCodes.Status400BadRequest);
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
        var invokedFromQuery = request?.Parent is null || string.IsNullOrEmpty(request.Parent.Id);
        if (invokedFromQuery && !SelectionRuleParser.Parse(definition.SelectionRule)(selectedCount))
        {
            return ClientResult.Envelope(clientAccessor,
                new { error = $"Action '{actionName}' requires a selection of '{definition.SelectionRule}'; {selectedCount} items were submitted." },
                StatusCodes.Status400BadRequest);
        }

        // D12 (#467): a selection is resolved through the query it was ticked in, so that query's
        // OnDisableActionsAsync decision always applies. Omitting the query used to skip it.
        if (selectedCount > 0 && string.IsNullOrEmpty(request?.QueryId))
        {
            return ClientResult.Envelope(clientAccessor,
                new { error = $"Action '{actionName}' on a selection must name the query its rows were selected in (queryId)." },
                StatusCodes.Status400BadRequest);
        }

        RetryScope.Accept(retryAccessor, request);

        try
        {
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
                $"action '{actionName}' on '{entityType.Name}' ({(request?.SelectedItemIds?.Length ?? 0)} selected)");

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
            Po? parent = null;
            if (request?.Parent is { } submittedParent && !string.IsNullOrEmpty(submittedParent.Id))
            {
                parent = await databaseAccess.GetPersistentObjectAsync(entityType.Id, submittedParent.Id);
                if (parent is null)
                {
                    return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
                }
            }

            // The sub-query's container, when there is one. Resolved under ITS OWN type, which is
            // the one place that is correct rather than a C3 violation: the container is a
            // different type from the action's by construction (Cars listed on a Company page), so
            // loading it under the route type would hand a Company id to the Car collection guard
            // and refuse every time. Safety comes from GetPersistentObjectAsync applying that
            // type's own Read gate and row rule — a container the caller may not see refuses the
            // request rather than arriving as a fact.
            Po? queryParent = null;
            string? queryParentTypeName = null;
            if (!string.IsNullOrEmpty(request?.ParentId) && !string.IsNullOrEmpty(request?.ParentType))
            {
                var parentTypeDefinition = modelLoader.ResolveEntityType(request.ParentType);
                if (parentTypeDefinition is null)
                {
                    return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
                }

                // Live rows only, deliberately (#460). The query reads take a `parentDeleted` mode so a
                // deleted object opened from the recycle bin can list its sub-queries; this endpoint
                // does not, because that page offers no actions — a deleted container is refused here.
                queryParent = await databaseAccess.GetPersistentObjectAsync(parentTypeDefinition.Id, request.ParentId);
                if (queryParent is null)
                {
                    return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
                }

                queryParentTypeName = parentTypeDefinition.Name;
            }

            // Selected items come from this type's list screen; an id-less one names no row and
            // cannot be verified, so it fails the whole request rather than being skipped.
            var submittedIds = (request?.SelectedItemIds ?? []).ToList();
            if (submittedIds.Any(string.IsNullOrEmpty))
            {
                return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
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
                : await MaterializeSelectionAsync(request, entityType, submittedIds!, queryParent, httpContext);

            if (selectedItems is null)
            {
                return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
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
                return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
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
                return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
            }

            // Disabled-action gate (#460, D13) — after every row gate above, so a row the caller may
            // not see is still a 404 and only a visible one can answer 403. The same hook that filled
            // DisabledActions when the page loaded is asked about every target this action touches:
            // the object it runs on, the query it was invoked from, and each selected row. The union
            // decides, because a button hidden on any of them was never offered for this request.
            var disabled = await EvaluateDisabledAsync(
                entityType, clrType, actionName, parent, request, queryParent, queryParentTypeName, selectedItems);
            if (disabled.Contains(actionName))
            {
                return ClientResult.ActionDisabled(clientAccessor, new SparkActionDisabledException(actionName));
            }

            var args = new CustomActionArgs
            {
                Parent = parent,
                QueryParent = queryParent,
                QueryParentType = queryParentTypeName,
                SelectedItems = [.. selectedItems],
                SubmittedParent = request?.Parent,
                SubmittedSelectedItemIds = [.. submittedIds],
            };

            await action.ExecuteAsync(args, httpContext.RequestAborted);

            // A persistent object an action hands back is a presentation like any other (M2c-2a). It is
            // not presented here any more: loaded or scaffolded, it was built for this caller (D13a),
            // and one the action made itself is caught by the boundary net.

            // T5 (#460): whatever the action handed to SetResult, as the envelope's result. Null when
            // it set nothing, which is the shape every existing caller already reads.
            return ClientResult.Envelope(clientAccessor, args.Result, StatusCodes.Status200OK);
        }
        catch (SparkAccessDeniedException)
        {
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
        }
        catch (SparkActionDisabledException ex)
        {
            // Raised by a write the action made through IDatabaseAccess, on an object whose own hook
            // disables that write. A 403 naming it, rather than the catch-all's anonymous 500.
            return ClientResult.ActionDisabled(clientAccessor, ex);
        }
        catch (SparkValidationException ex)
        {
            // A write the action made through IDatabaseAccess that an interceptor refused for a
            // readable reason (a locked post, #460 M12 spike S-MOD-D): a 400 like /po/update gives,
            // not the catch-all's anonymous 500.
            return ClientResult.Envelope(clientAccessor, new { errors = new[] { ex.ToError() } }, StatusCodes.Status400BadRequest);
        }
        catch (SparkThrottledException ex)
        {
            return ClientResult.Throttled(clientAccessor, httpContext, ex);
        }
        // ⚠️ The filter is load-bearing, and this is the only endpoint that needs one. A retry is not
        // a failure — it is the server asking the caller a question, and the middleware turns it into
        // a 449. Every other retry-capable endpoint catches only specific exception types, so the
        // exception simply propagates; this one has a catch-all, which would swallow the prompt and
        // answer 500 instead. `RetryFromEveryHookTests` is what fails if this filter is removed.
        catch (Exception ex) when (ex is not SparkRetryActionException)
        {
            // R2-M1: server-side log with full detail, generic public response.
            logger.LogError(ex, "Custom action '{ActionName}' failed for entity type '{EntityType}'", actionName, entityType.Name);
            return ClientResult.Envelope(clientAccessor, new { error = "Operation failed" }, StatusCodes.Status500InternalServerError);
        }
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
        Po? parent,
        CustomActionRequest? request,
        Po? queryParent,
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

        if (!string.IsNullOrEmpty(request?.QueryId)
            && queryLoader.ResolveQuery(request.QueryId) is { } query
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
        CustomActionRequest? request,
        EntityTypeDefinition entityType,
        IReadOnlyList<string> submittedIds,
        Po? queryParent,
        HttpContext httpContext)
    {
        if (string.IsNullOrEmpty(request?.QueryId) || queryLoader.ResolveQuery(request.QueryId) is not { } query)
            return null;

        return await selectionResolver.ResolveAsync(entityType, query, queryParent, submittedIds, httpContext.RequestAborted);
    }
}
