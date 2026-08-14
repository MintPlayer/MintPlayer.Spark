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
    [Inject] private readonly ICustomActionResolver actionResolver;
    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly IRetryAccessor retryAccessor;
    [Inject] private readonly IClientAccessor clientAccessor;
    [Inject] private readonly ILogger<ExecuteCustomAction> logger;
    [Inject] private readonly IDatabaseAccess databaseAccess;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var objectTypeId = httpContext.Request.RouteValues["objectTypeId"]?.ToString()!;
        var actionName = httpContext.Request.RouteValues["actionName"]?.ToString()!;

        var entityType = modelLoader.ResolveEntityType(objectTypeId);
        if (entityType is null)
        {
            return ClientResult.Envelope(clientAccessor, new { error = $"Entity type '{objectTypeId}' not found" }, StatusCodes.Status404NotFound);
        }

        var typeName = entityType.ClrType.Split('.').Last();

        try
        {
            await permissionService.EnsureAuthorizedAsync(actionName, typeName);
        }
        catch (SparkAccessDeniedException)
        {
            var isAuthed = httpContext.User.Identity?.IsAuthenticated == true;
            return ClientResult.Envelope(clientAccessor,
                new { error = isAuthed ? "Access denied" : "Authentication required" },
                isAuthed ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized);
        }

        var action = actionResolver.Resolve(actionName);
        if (action is null)
        {
            return ClientResult.Envelope(clientAccessor, new { error = $"Custom action '{actionName}' not found" }, StatusCodes.Status404NotFound);
        }

        var request = await httpContext.Request.ReadFromJsonAsync<CustomActionRequest>();

        if (request?.RetryResults is { Length: > 0 } retryResults)
        {
            var accessor = (RetryAccessor)retryAccessor;
            accessor.AnsweredResults = retryResults.ToDictionary(r => r.Step);
        }

        try
        {
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
                    return ClientResult.Envelope(clientAccessor, new { error = "Not found" }, StatusCodes.Status404NotFound);
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
                    return ClientResult.Envelope(clientAccessor, new { error = "Not found" }, StatusCodes.Status404NotFound);
                }
                selectedItems.Add(loaded);
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
            var isAuthed = httpContext.User.Identity?.IsAuthenticated == true;
            return ClientResult.Envelope(clientAccessor,
                new { error = isAuthed ? "Access denied" : "Authentication required" },
                isAuthed ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized);
        }
        catch (Exception ex)
        {
            // R2-M1: server-side log with full detail, generic public response.
            logger.LogError(ex, "Custom action '{ActionName}' failed for entity type '{EntityType}'", actionName, objectTypeId);
            return ClientResult.Envelope(clientAccessor, new { error = "Operation failed" }, StatusCodes.Status500InternalServerError);
        }
    }
}
