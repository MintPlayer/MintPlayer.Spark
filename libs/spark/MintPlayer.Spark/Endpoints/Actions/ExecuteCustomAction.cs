using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
// The sibling namespace MintPlayer.Spark.Endpoints.PersistentObject shadows the type name here.
using Po = MintPlayer.Spark.Abstractions.PersistentObject;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Exceptions;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.Actions;

internal sealed partial class ExecuteCustomAction : IPostEndpoint, IMemberOf<ActionsGroup>
{
    public static string Path => "/{objectTypeId}/{actionName}";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder)
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
    private const int MaxSelectedItems = 200;
    [Inject] private readonly ICustomActionResolver actionResolver;
    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly IRetryAccessor retryAccessor;
    [Inject] private readonly IClientAccessor clientAccessor;
    [Inject] private readonly ILogger<ExecuteCustomAction> logger;
    [Inject] private readonly IDatabaseAccess databaseAccess;
    // The request-scoped session — the same instance IDatabaseAccess uses, so an IgnoreMaxRequests
    // scope opened here covers the row-gated loads below.
    [Inject] private readonly Raven.Client.Documents.Session.IAsyncDocumentSession session;
    [Inject] private readonly ICustomActionsConfigurationLoader configLoader;
    [Inject] private readonly IQueryLoader queryLoader;
    [Inject] private readonly IQueryExecutor queryExecutor;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var objectTypeId = httpContext.Request.RouteValues["objectTypeId"]?.ToString()!;
        var actionName = httpContext.Request.RouteValues["actionName"]?.ToString()!;

        var entityType = modelLoader.ResolveEntityType(objectTypeId);
        if (entityType is null)
        {
            // Same shape as a denial. This ran BEFORE the grant check below, so a specific
            // 404 here against a 401 there told an anonymous caller which entity types are
            // real -- the M-3 oracle, in the one endpoint the sweep missed.
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
        }

        var typeName = entityType.ClrType?.Split('.').Last() ?? entityType.Name;

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
        // retired by removing it from customActions.json (the documented way) — was still callable
        // by name. Gate on the configuration, exactly as ListCustomActions does: absent → 404.
        var configuration = configLoader.GetConfiguration();
        if (!configuration.Keys.Contains(actionName, StringComparer.OrdinalIgnoreCase))
        {
            return ClientResult.Envelope(clientAccessor, new { error = $"Custom action '{actionName}' not found" }, StatusCodes.Status404NotFound);
        }

        var definition = configuration.First(kv => kv.Key.Equals(actionName, StringComparison.OrdinalIgnoreCase)).Value;

        var action = actionResolver.Resolve(actionName);
        if (action is null)
        {
            return ClientResult.Envelope(clientAccessor, new { error = $"Custom action '{actionName}' not found" }, StatusCodes.Status404NotFound);
        }

        var request = await httpContext.Request.ReadFromJsonAsync<CustomActionRequest>();

        var selectedCount = request?.SelectedItemIds?.Length ?? 0;

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

        if (request?.RetryResults is { Length: > 0 } retryResults)
        {
            var accessor = (RetryAccessor)retryAccessor;
            accessor.AnsweredResults = retryResults.ToDictionary(r => r.Step);
        }

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
            using var _ = session.IgnoreMaxRequests(ActionRequestBudget, logger);

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
            // Falls back to the document load for the three shapes that cannot be re-run: a query
            // owning its own paging, a streaming query, and a request naming no query.
            var selectedItems = submittedIds.Count == 0
                ? []
                : await MaterializeSelectionAsync(request, entityType, submittedIds!, httpContext);

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

            var clrType = typeResolver.Resolve(entityType.ClrType);
            if (rowIds.Length > 0 && clrType is not null &&
                !await rowSecurity.AreAllowedAsync(session, clrType, actionName, rowIds))
            {
                return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
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
            return ClientResult.Envelope(clientAccessor, null, StatusCodes.Status200OK);
        }
        catch (SparkRetryActionException ex)
        {
            return ClientResult.Retry(clientAccessor, ex);
        }
        catch (SparkAccessDeniedException)
        {
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
        }
        catch (Exception ex)
        {
            // R2-M1: server-side log with full detail, generic public response.
            logger.LogError(ex, "Custom action '{ActionName}' failed for entity type '{EntityType}'", actionName, objectTypeId);
            return ClientResult.Envelope(clientAccessor, new { error = "Operation failed" }, StatusCodes.Status500InternalServerError);
        }
    }
    /// <summary>
    /// The selected rows, re-materialized server-side. <see langword="null"/> means refuse.
    /// </summary>
    /// <remarks>
    /// Two paths, and the choice is made from the query's shape <em>before</em> anything runs, not by
    /// trying and failing:
    /// <list type="number">
    /// <item><description><b>Re-run the query</b> narrowed to these ids. The rows then carry the
    /// query's own projection — index-computed columns included — and a composed query works at all.
    /// The executor enforces the <c>Query</c> right independently, so the client-supplied query id is
    /// narrowing-only, and the entity-type check below stops it naming another type's rows.</description></item>
    /// <item><description><b>Load the documents</b> and project them, for a query that owns its own
    /// paging, a streaming query, or a request naming no query. These lose index-computed values —
    /// stated in the guide rather than papered over.</description></item>
    /// </list>
    /// </remarks>
    private async Task<IReadOnlyList<QueryResultItem>?> MaterializeSelectionAsync(
        CustomActionRequest? request,
        EntityTypeDefinition entityType,
        IReadOnlyList<string> submittedIds,
        HttpContext httpContext)
    {
        if (!string.IsNullOrEmpty(request?.QueryId) && queryLoader.ResolveQuery(request.QueryId) is { } query)
        {
            // The query must produce rows of the type this action is authorized on. Without this the
            // client could name a query over another type and have its rows handed to an action
            // gated on a different grant.
            if (!string.Equals(query.EntityType, entityType.Name, StringComparison.OrdinalIgnoreCase))
                return null;

            if (IsReExecutable(query))
            {
                var restricted = await queryExecutor.ExecuteQueryAsync(
                    query,
                    parent: request.Parent,
                    skip: 0,
                    take: submittedIds.Count,
                    search: null,
                    restrictToIds: submittedIds,
                    cancellationToken: httpContext.RequestAborted);

                return restricted.Items;
            }
        }

        // Fallback: the batched row-gated load, projected onto the same shape.
        var loaded = await databaseAccess.GetPersistentObjectsByIdAsync(entityType.Id, submittedIds);
        var columns = QueryResultProjector.BuildColumns(entityType);
        return QueryResultProjector.ToItems(loaded, columns, $"Action '{entityType.Name}'");
    }

    /// <summary>
    /// Whether a query can be re-run narrowed to a set of ids.
    /// </summary>
    /// <remarks>
    /// A streaming query's method takes <c>(StreamingQueryArgs, CancellationToken)</c> and is not
    /// resolvable as a custom query at all. A query returning <c>SparkQueryPage&lt;T&gt;</c> owns its
    /// own filtering and paging, and there is no way to ask it for "the page containing these ids".
    /// Both are properties of the declaration, so this is a branch rather than a failed attempt.
    /// </remarks>
    private bool IsReExecutable(SparkQuery query)
        => !query.IsStreamingQuery && !queryExecutor.OwnsItsOwnPaging(query);

}
