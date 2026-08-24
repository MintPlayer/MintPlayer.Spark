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

internal sealed partial class UpdatePersistentObject : IPutEndpoint, IMemberOf<PersistentObjectGroup>
{
    public static string Path => "/{objectTypeId}/{**id}";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder)
    {
        builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));
    }

    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IValidationService validationService;
    [Inject] private readonly IRefreshInvoker refreshInvoker;
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

        try
        {
            var decodedId = Uri.UnescapeDataString(id);
            var existingObj = await databaseAccess.GetPersistentObjectAsync(entityType.Id, decodedId);

            if (existingObj is null)
            {
                return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
            }

            var request = await httpContext.Request.ReadFromJsonAsync<PersistentObjectRequest>()
                ?? throw new InvalidOperationException("Request could not be deserialized from the request body.");

            var obj = request.PersistentObject
                ?? throw new InvalidOperationException("PersistentObject is required.");

            if (request.RetryResults is { Length: > 0 } retryResults)
            {
                var accessor = (RetryAccessor)retryAccessor;
                accessor.AnsweredResults = retryResults.ToDictionary(r => r.Step);
            }

            obj.Id = existingObj.Id;
            obj.ObjectTypeId = entityType.Id;

            // Authorize before validating — see the note in Create.cs (N23).
            await databaseAccess.EnsureSaveAuthorizedAsync(obj);

            // Validate against the object as the refresh hook shapes it, not as the model declares
            // it. A hook that makes a field required has changed the contract, and validating the
            // raw model would enforce a different one than the user was shown. Re-deriving here —
            // rather than trusting what the client posted — is also what stops a client from
            // escaping the hook by never calling /refresh.
            var effective = await refreshInvoker.BuildEffectiveAsync(entityType, obj, httpContext.RequestAborted);
            var validationResult = validationService.ValidateEffective(effective);
            if (!validationResult.IsValid)
            {
                return ClientResult.Envelope(clientAccessor, new { errors = validationResult.Errors }, 400);
            }

            var result = await databaseAccess.SavePersistentObjectAsync(obj);
            return ClientResult.Envelope(clientAccessor, result, 200);
        }
        catch (SparkConcurrencyException)
        {
            // R2-M1: SparkConcurrencyException.Message contains the server-side
            // change vector — useful for the legitimate optimistic-concurrency
            // recovery flow, but it leaks document-version state that an
            // attacker can use as a side channel. Return a generic 409; clients
            // know to re-fetch on 409 regardless of the body content.
            return ClientResult.Envelope(clientAccessor, new { error = "Concurrency conflict" }, 409);
        }
        catch (SparkValidationException ex)
        {
            return ClientResult.Envelope(clientAccessor, new { errors = new[] { ex.ToError() } }, 400);
        }
        catch (SparkRetryActionException ex)
        {
            return ClientResult.Retry(clientAccessor, ex);
        }
        catch (SparkRowLevelAccessDeniedException)
        {
            // R2-H2: row-level denial returns 404 to match the read path —
            // M-3 says authorized-but-forbidden must be indistinguishable from
            // not-found for instance-level checks.
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
        }
        catch (SparkAccessDeniedException)
        {
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
        }
    }
}
