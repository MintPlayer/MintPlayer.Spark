using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Actions;
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

        var args = new CustomActionArgs
        {
            Parent = request?.Parent,
            SelectedItems = request?.SelectedItems ?? [],
        };

        try
        {
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
            logger.LogError(ex, "Custom action '{ActionName}' failed for entity type '{EntityType}'", actionName, objectTypeId);
            return ClientResult.Envelope(clientAccessor, new { error = ex.Message }, StatusCodes.Status500InternalServerError);
        }
    }
}
