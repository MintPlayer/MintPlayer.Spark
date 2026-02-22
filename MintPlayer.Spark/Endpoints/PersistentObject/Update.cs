using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Exceptions;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.PersistentObject;

[Register(ServiceLifetime.Scoped)]
public sealed partial class UpdatePersistentObject
{
    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IValidationService validationService;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IRetryAccessor retryAccessor;

    public async Task HandleAsync(HttpContext httpContext, string objectTypeId, string id)
    {
        var entityType = modelLoader.ResolveEntityType(objectTypeId);
        if (entityType is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await httpContext.Response.WriteAsJsonAsync(new { error = $"Entity type '{objectTypeId}' not found" });
            return;
        }

        try
        {
            var decodedId = Uri.UnescapeDataString(id);
            var existingObj = await databaseAccess.GetPersistentObjectAsync(entityType.Id, decodedId);

            if (existingObj is null)
            {
                httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
                await httpContext.Response.WriteAsJsonAsync(new { error = $"Object with ID {decodedId} not found" });
                return;
            }

            var request = await httpContext.Request.ReadFromJsonAsync<PersistentObjectRequest>()
                ?? throw new InvalidOperationException("Request could not be deserialized from the request body.");

            var obj = request.PersistentObject
                ?? throw new InvalidOperationException("PersistentObject is required.");

            // Set up retry state if this is a re-invocation
            if (request.RetryResults is { Length: > 0 } retryResults)
            {
                var accessor = (RetryAccessor)retryAccessor;
                accessor.AnsweredResults = retryResults.ToDictionary(r => r.Step);
            }

            // Ensure the ID and ObjectTypeId match the URL parameters
            obj.Id = existingObj.Id;
            obj.ObjectTypeId = entityType.Id;

            // Validate the object
            var validationResult = validationService.Validate(obj);
            if (!validationResult.IsValid)
            {
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                await httpContext.Response.WriteAsJsonAsync(new { errors = validationResult.Errors });
                return;
            }

            var result = await databaseAccess.SavePersistentObjectAsync(obj);
            await httpContext.Response.WriteAsJsonAsync(result);
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
        }
    }
}
