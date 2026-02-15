using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Exceptions;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.PersistentObject;

[Register(ServiceLifetime.Scoped)]
public sealed partial class DeletePersistentObject
{
    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IRetryAccessor retryAccessor;
    [Inject] private readonly IAccessControl? accessControl;

    public async Task HandleAsync(HttpContext httpContext, string objectTypeId, string id)
    {
        var entityType = modelLoader.ResolveEntityType(objectTypeId);
        if (entityType is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await httpContext.Response.WriteAsJsonAsync(new { error = $"Entity type '{objectTypeId}' not found" });
            return;
        }

        // Authorization check (only when IAccessControl is registered)
        if (accessControl is not null)
        {
            if (!await accessControl.IsAllowedAsync($"Delete/{entityType.ClrType}"))
            {
                httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
                await httpContext.Response.WriteAsJsonAsync(new { error = "Access denied" });
                return;
            }
        }

        var decodedId = Uri.UnescapeDataString(id);
        var obj = await databaseAccess.GetPersistentObjectAsync(entityType.Id, decodedId);

        if (obj is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await httpContext.Response.WriteAsJsonAsync(new { error = $"Object with ID {decodedId} not found" });
            return;
        }

        // Read retry state from body if present (body is normally empty for DELETE)
        if (httpContext.Request.ContentLength > 0)
        {
            var request = await httpContext.Request.ReadFromJsonAsync<PersistentObjectRequest>();
            if (request?.RetryResults is { Length: > 0 } retryResults)
            {
                var accessor = (RetryAccessor)retryAccessor;
                accessor.AnsweredResults = retryResults.ToDictionary(r => r.Step);
            }
        }

        try
        {
            await databaseAccess.DeletePersistentObjectAsync(entityType.Id, decodedId);
            httpContext.Response.StatusCode = StatusCodes.Status204NoContent;
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
