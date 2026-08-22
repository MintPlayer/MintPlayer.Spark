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
    [Inject] private readonly IClientAccessor clientAccessor;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var objectTypeId = httpContext.Request.RouteValues["objectTypeId"]!.ToString()!;

        var entityType = modelLoader.ResolveEntityType(objectTypeId);
        if (entityType is null)
        {
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
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
        // R2-M18: POST is "Create", never "Edit". Force Id=null so SavePersistentObjectAsync's
        // string.IsNullOrEmpty(Id) branch always picks "New" — a client posting
        // {"Id":"cars/existing"} used to flip the action to Edit and overwrite a
        // foreign record under the POST verb, bypassing the entity-type-level "New"
        // permission and any developer mental-model of "POST = creation".
        obj.Id = null;

        try
        {
            // Authorize before validating (N23). The other order told a caller with no right to
            // create this type which of its attributes were invalid — a refusal either way, but one
            // that answers a question the caller was not entitled to ask. The decision itself still
            // belongs to DatabaseAccess; this only asks it earlier.
            await databaseAccess.EnsureSaveAuthorizedAsync(obj);

            var validationResult = validationService.Validate(obj);
            if (!validationResult.IsValid)
            {
                return ClientResult.Envelope(clientAccessor, new { errors = validationResult.Errors }, 400);
            }

            var result = await databaseAccess.SavePersistentObjectAsync(obj);
            return ClientResult.Envelope(clientAccessor, result, 201);
        }
        catch (SparkValidationException ex)
        {
            return ClientResult.Envelope(clientAccessor, new { errors = new[] { ex.ToError() } }, 400);
        }
        catch (SparkRetryActionException ex)
        {
            return ClientResult.Retry(clientAccessor, ex);
        }
        catch (SparkAccessDeniedException)
        {
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
        }
    }
}
