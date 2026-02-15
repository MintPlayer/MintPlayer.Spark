using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Exceptions;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.PersistentObject;

[Register(ServiceLifetime.Scoped)]
public sealed partial class CreatePersistentObject
{
    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IValidationService validationService;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IRetryAccessor retryAccessor;

    public async Task HandleAsync(HttpContext httpContext, string objectTypeId)
    {
        var entityType = modelLoader.ResolveEntityType(objectTypeId);
        if (entityType is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await httpContext.Response.WriteAsJsonAsync(new { error = $"Entity type '{objectTypeId}' not found" });
            return;
        }

        var request = await httpContext.Request.ReadFromJsonAsync<PersistentObjectRequest>()
            ?? throw new InvalidOperationException("Request could not be deserialized from the request body.");

        var obj = request.PersistentObject
            ?? throw new InvalidOperationException("PersistentObject is required.");

        // Set up retry state if this is a re-invocation
        if (request.RetryResult is { } retryResult)
        {
            var accessor = (RetryAccessor)retryAccessor;
            accessor.AnsweredStep = retryResult.Step;
            accessor.AnsweredResult = retryResult;
        }

        // Ensure the ObjectTypeId matches the resolved entity type
        obj.ObjectTypeId = entityType.Id;

        // Validate the object
        var validationResult = validationService.Validate(obj);
        if (!validationResult.IsValid)
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsJsonAsync(new { errors = validationResult.Errors });
            return;
        }

        try
        {
            var result = await databaseAccess.SavePersistentObjectAsync(obj);

            httpContext.Response.StatusCode = StatusCodes.Status201Created;
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
    }
}
