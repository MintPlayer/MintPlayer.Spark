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

internal sealed partial class DeletePersistentObject : IDeleteEndpoint, IMemberOf<PersistentObjectGroup>
{
    public static string Path => "/{objectTypeId}/{**id}";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder)
    {
        builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));
    }

    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IRetryAccessor retryAccessor;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var objectTypeId = httpContext.Request.RouteValues["objectTypeId"]!.ToString()!;
        var id = httpContext.Request.RouteValues["id"]!.ToString()!;

        var entityType = modelLoader.ResolveEntityType(objectTypeId);
        if (entityType is null)
        {
            return Results.Json(new { error = $"Entity type '{objectTypeId}' not found" }, statusCode: 404);
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
            var decodedId = Uri.UnescapeDataString(id);
            var obj = await databaseAccess.GetPersistentObjectAsync(entityType.Id, decodedId);

            if (obj is null)
            {
                return Results.Json(new { error = $"Object with ID {decodedId} not found" }, statusCode: 404);
            }

            await databaseAccess.DeletePersistentObjectAsync(entityType.Id, decodedId);
            return Results.NoContent();
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
