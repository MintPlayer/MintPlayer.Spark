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
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
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
                return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
            }

            await databaseAccess.DeletePersistentObjectAsync(entityType.Id, decodedId);
            return ClientResult.Envelope(clientAccessor, null, 204);
        }
        catch (SparkValidationException ex)
        {
            // A delete can be refused for a business reason too — "this client still has live
            // tokens", say. Same envelope, so the screen shows it the same way.
            return ClientResult.Envelope(clientAccessor, new { errors = new[] { ex.ToError() } }, 400);
        }
        catch (SparkRetryActionException ex)
        {
            return ClientResult.Retry(clientAccessor, ex);
        }
        catch (SparkRowLevelAccessDeniedException)
        {
            // R2-H2: row-level Delete denial returns 404 (M-3 uniformity).
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
        }
        catch (SparkAccessDeniedException)
        {
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
        }
    }
}
