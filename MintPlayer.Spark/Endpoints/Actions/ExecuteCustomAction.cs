using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Exceptions;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.Actions;

[Register(ServiceLifetime.Scoped)]
internal sealed partial class ExecuteCustomAction
{
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly ICustomActionResolver actionResolver;
    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly IRetryAccessor retryAccessor;

    public async Task HandleAsync(HttpContext httpContext, string objectTypeId, string actionName)
    {
        var entityType = modelLoader.ResolveEntityType(objectTypeId);
        if (entityType is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await httpContext.Response.WriteAsJsonAsync(new { error = $"Entity type '{objectTypeId}' not found" });
            return;
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
                httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await httpContext.Response.WriteAsJsonAsync(new { error = "Authentication required" });
            }
            else
            {
                httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
                await httpContext.Response.WriteAsJsonAsync(new { error = "Access denied" });
            }
            return;
        }

        // Resolve the custom action implementation
        var action = actionResolver.Resolve(actionName);
        if (action is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await httpContext.Response.WriteAsJsonAsync(new { error = $"Custom action '{actionName}' not found" });
            return;
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
            httpContext.Response.StatusCode = StatusCodes.Status200OK;
        }
        catch (SparkRetryActionException ex)
        {
            httpContext.Response.StatusCode = 449;
            await httpContext.Response.WriteAsJsonAsync(new
            {
                type = "retry-action",
                step = ex.Step,
                title = ex.Title,
                message = ex.RetryMessage,
                options = ex.Options,
                defaultOption = ex.DefaultOption,
                persistentObject = ex.PersistentObject,
            });
        }
    }
}
