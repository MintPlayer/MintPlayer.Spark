using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.ClientOperations;
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
    [Inject] private readonly IClientAccessor clientAccessor;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var objectTypeId = httpContext.Request.RouteValues["objectTypeId"]!.ToString()!;
        var id = httpContext.Request.RouteValues["id"]!.ToString()!;

        var entityType = modelLoader.ResolveEntityType(objectTypeId);
        if (entityType is null)
        {
            return ClientResult.Envelope(clientAccessor, new { error = $"Entity type '{objectTypeId}' not found" }, 404);
        }

        // Read retry state from body if present (DELETE may carry JSON on retry resubmission).
        // Use Content-Type rather than Content-Length to handle chunked transfer-encoding.
        if (httpContext.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true)
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
                return ClientResult.Envelope(clientAccessor, new { error = $"Object with ID {decodedId} not found" }, 404);
            }

            await databaseAccess.DeletePersistentObjectAsync(entityType.Id, decodedId);
            return ClientResult.Envelope(clientAccessor, null, 204);
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
                isAuthed ? 403 : 401);
        }
    }
}
