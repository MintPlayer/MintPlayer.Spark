using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Exceptions;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.PersistentObject;

internal sealed partial class CreatePersistentObject : IPostEndpoint, IMemberOf<PersistentObjectGroup>
{
    public static string Path => "/{objectTypeId}";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder)
    {
        builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));
    }

    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IValidationService validationService;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IRetryAccessor retryAccessor;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var objectTypeId = httpContext.Request.RouteValues["objectTypeId"]!.ToString()!;

        var entityType = modelLoader.ResolveEntityType(objectTypeId);
        if (entityType is null)
        {
            return Results.Json(new { error = $"Entity type '{objectTypeId}' not found" }, statusCode: 404);
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

        // Ensure the ObjectTypeId matches the resolved entity type
        obj.ObjectTypeId = entityType.Id;

        // Validate the object
        var validationResult = validationService.Validate(obj);
        if (!validationResult.IsValid)
        {
            return Results.Json(new { errors = validationResult.Errors }, statusCode: 400);
        }

        try
        {
            var result = await databaseAccess.SavePersistentObjectAsync(obj);
            return Results.Json(result, statusCode: 201);
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
                return Results.Json(new { error = "Authentication required" }, statusCode: 401);
            }
            else
            {
                return Results.Json(new { error = "Access denied" }, statusCode: 403);
            }
        }
    }
}
