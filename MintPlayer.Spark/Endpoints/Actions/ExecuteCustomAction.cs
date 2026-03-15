using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.Authorization;
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
    [Inject] private readonly ILogger<ExecuteCustomAction> logger;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var objectTypeId = httpContext.Request.RouteValues["objectTypeId"]?.ToString()!;
        var actionName = httpContext.Request.RouteValues["actionName"]?.ToString()!;

        var entityType = modelLoader.ResolveEntityType(objectTypeId);
        if (entityType is null)
        {
            return Results.Json(new { error = $"Entity type '{objectTypeId}' not found" }, statusCode: StatusCodes.Status404NotFound);
        }

        // Get the simple type name (e.g., "Car" from "Fleet.Entities.Car")
        var typeName = entityType.ClrType.Split('.').Last();

        // Authorization check
        try
        {
            await permissionService.EnsureAuthorizedAsync(actionName, typeName);
        }
        catch (SparkAccessDeniedException)
        {
            if (httpContext.User.Identity?.IsAuthenticated != true)
            {
                return Results.Json(new { error = "Authentication required" }, statusCode: StatusCodes.Status401Unauthorized);
            }
            else
            {
                return Results.Json(new { error = "Access denied" }, statusCode: StatusCodes.Status403Forbidden);
            }
        }

        // Resolve the custom action implementation
        var action = actionResolver.Resolve(actionName);
        if (action is null)
        {
            return Results.Json(new { error = $"Custom action '{actionName}' not found" }, statusCode: StatusCodes.Status404NotFound);
        }

        // Deserialize request body
        var request = await httpContext.Request.ReadFromJsonAsync<CustomActionRequest>();

        // Set up retry state if this is a re-invocation
        if (request?.RetryResults is { Length: > 0 } retryResults)
        {
            var accessor = (RetryAccessor)retryAccessor;
            accessor.AnsweredResults = retryResults.ToDictionary(r => r.Step);
        }

        var args = new CustomActionArgs
        {
            Parent = request?.Parent,
            SelectedItems = request?.SelectedItems ?? [],
        };

        try
        {
            await action.ExecuteAsync(args, httpContext.RequestAborted);
            return Results.Ok();
        }
        catch (SparkRetryActionException ex)
        {
            return Results.Json(new
            {
                type = "retry-action",
                step = ex.Step,
                title = ex.Title,
                message = ex.RetryMessage,
                options = ex.Options,
                defaultOption = ex.DefaultOption,
                persistentObject = ex.PersistentObject,
            }, statusCode: 449);
        }
        catch (SparkAccessDeniedException)
        {
            if (httpContext.User.Identity?.IsAuthenticated != true)
            {
                return Results.Json(new { error = "Authentication required" }, statusCode: StatusCodes.Status401Unauthorized);
            }
            else
            {
                return Results.Json(new { error = "Access denied" }, statusCode: StatusCodes.Status403Forbidden);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Custom action '{ActionName}' failed for entity type '{EntityType}'", actionName, objectTypeId);
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
