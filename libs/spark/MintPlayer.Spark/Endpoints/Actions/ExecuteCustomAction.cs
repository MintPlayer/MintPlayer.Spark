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

        var selectedCount = request?.SelectedItems?.Length ?? 0;

        // A hard ceiling on the selection, whether or not a rule is declared.
        //
        // This is NOT belt-and-braces for the rule below. IgnoreMaxRequests sets
        // MaxNumberOfRequestsPerSession to int.MaxValue for the whole handler, and the
        // "estimatedRequests" figure it is handed is only a logging threshold — one that
        // is itself computed from SelectedItems.Length, so the warning fires later the
        // larger the abuse. Each selected id then costs a document load, a collection-guard
        // check, a row-rule evaluation, breadcrumb resolution and redaction. Without this,
        // any caller holding one action grant can turn a single request into unbounded
        // server work, and no rate limiter is on this route by default.
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
            // #239 M5: resolving each selected item is a full row-gated load (load + breadcrumbs)
            // on the shared request session, so a multi-select action is a per-item N+1 that hit
            // RavenDB's 30-cap around ~5 items. A user-invoked bulk action legitimately needs the
            // round-trips, so lift the ceiling for this whole handler — sized to the item count and
            // logged if the action overruns, so it stays a deliberate, visible budget rather than a
            // silent one.
            var estimatedRequests = 30 + (1 + (request?.SelectedItems?.Length ?? 0)) * 6;
            using var _ = session.IgnoreMaxRequests(estimatedRequests, logger);

            // Row-gated server-side resolution (#236 G3). The wire's Parent/SelectedItems are
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
            // SelectedItems already does this correctly below.
            Po? parent = null;
            if (request?.Parent is { } submittedParent && !string.IsNullOrEmpty(submittedParent.Id))
            {
                parent = await databaseAccess.GetPersistentObjectAsync(entityType.Id, submittedParent.Id);
                if (parent is null)
                {
                    return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
                }
            }

            var selectedItems = new List<Po>();
            foreach (var submitted in request?.SelectedItems ?? [])
            {
                // Selected items come from this type's list screen; an id-less one names no row
                // and cannot be verified.
                var loaded = string.IsNullOrEmpty(submitted.Id)
                    ? null
                    : await databaseAccess.GetPersistentObjectAsync(entityType.Id, submitted.Id);
                if (loaded is null)
                {
                    return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
                }
                selectedItems.Add(loaded);
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
            var rowIds = selectedItems.Select(i => i.Id!)
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
                SelectedItems = [.. selectedItems],
                SubmittedParent = request?.Parent,
                SubmittedSelectedItems = request?.SelectedItems ?? [],
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
}
