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
    public static string Path => "/create";

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
    [Inject] private readonly IPermissionService permissionService;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        // The body has to be read before anything can be authorized, because the body is where the
        // type is. That inverts the old order, where the type-level "New" right was checked first so
        // that a caller with no right to create this type could not learn which types exist by POSTing
        // rubbish and comparing a 500 against a refusal (N23).
        //
        // The property survives because ReadAsync answers a malformed body exactly as it answers an
        // unknown type — null, refused below. A parse failure tells the caller only that their JSON
        // was bad, which they already knew.
        var (request, entityType) = await SparkRequestType.ReadAsync<PersistentObjectRequest>(httpContext, modelLoader);
        if (request is null || entityType is null)
        {
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
        }

        // Still in addition to the check EnsureSaveAuthorizedAsync makes below, and still free: the
        // permission service memoises per request.
        try
        {
            await permissionService.EnsureAuthorizedAsync("New", entityType.ClrType?.Split('.').Last() ?? entityType.Name);
        }
        catch (SparkAccessDeniedException)
        {
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
        }

        var obj = request.PersistentObject
            ?? throw new InvalidOperationException("PersistentObject is required.");

        // Set up retry state if this is a re-invocation
        RetryScope.Accept(retryAccessor, request);

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
