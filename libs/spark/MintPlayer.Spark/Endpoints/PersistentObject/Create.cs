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

[MemberOf<PersistentObjectGroup>]
internal sealed partial class CreatePersistentObject : IPostEndpoint<PersistentObjectRequest>
{
    public static string Path => "/create";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
    {
        builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));
    }

    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly ISaveValidation saveValidation;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IRetryAccessor retryAccessor;
    [Inject] private readonly IClientAccessor clientAccessor;
    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly ISaveResponsePresenter saveResponse;
    [Inject] private readonly IQueryLoader queryLoader;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    /// <summary>A body that cannot be bound gets the refusal an unusable request gets below, never a parse error (PRD D3a).</summary>
    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => new(ClientResult.EnvelopeRefusal(clientAccessor, context));

    public override async Task<IResult> HandleAsync(PersistentObjectRequest request, CancellationToken cancellationToken)
    {
        var httpContext = httpContextAccessor.HttpContext!;
        // The body has to be read before anything can be authorized, because the body is where the
        // type is. That inverts the old order, where the type-level "New" right was checked first so
        // that a caller with no right to create this type could not learn which types exist by POSTing
        // rubbish and comparing a 500 against a refusal (N23).
        //
        // The property survives because OnBindFailedAsync answers a body that cannot be bound exactly
        // as this answers an unknown type — the same refusal. A parse failure tells the caller only that
        // their JSON was bad, which they already knew.
        var entityType = SparkRequestType.Resolve(modelLoader, request);
        if (entityType is null)
        {
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
        }

        // Still in addition to the check EnsureSaveAuthorizedAsync makes below, and still free: the
        // permission service memoises per request.
        try
        {
            await permissionService.EnsureAuthorizedAsync("New", entityType.Name);
        }
        catch (SparkAccessDeniedException)
        {
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
        }

        // A body without the object is malformed: refused like one, not a 500.
        if (request.PersistentObject is not { } obj)
        {
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
        }

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

        // The parent a sub-query card's New was started from: resolved and authorized exactly as
        // /po/new resolved it, then handed to the save hooks as obj.Parent. A Parent in the body is the
        // caller's claim, so it is replaced either way — never the context a hook decides on.
        var (resolved, refused) = await SubQueryNewParent.ResolveAsync(
            modelLoader, queryLoader, databaseAccess, entityType, request.ParentId, request.ParentType, request.QueryId);
        if (refused)
        {
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
        }
        obj.Parent = resolved?.Parent;

        try
        {
            // Authorize before validating (N23). The other order told a caller with no right to
            // create this type which of its attributes were invalid — a refusal either way, but one
            // that answers a question the caller was not entitled to ask. The decision itself still
            // belongs to DatabaseAccess; this only asks it earlier.
            await databaseAccess.EnsureSaveAuthorizedAsync(obj);

            // Validated inside the save, right after the write shield (contributions M2d): against the
            // object as the refresh hook shapes it, not as the model declares it (a hook that makes a
            // field required changed the contract, and re-deriving it server-side is what stops a
            // client escaping the hook by never calling /refresh) — and only on the attributes the
            // caller may set, since a New-denied one keeps its CLR default whatever was posted.
            saveValidation.Request(obj, httpContext.RequestAborted);

            // Values for attributes the caller may not set on a create are dropped inside the save
            // (contributions M2c-2b, leak 7): the CLR default or initializer stays, and the save is not
            // refused, because a refusal would say which attributes exist.
            var result = await databaseAccess.SavePersistentObjectAsync(obj);

            // Re-presented as a load presents it, as Update does (leak 2).
            var presented = await saveResponse.PresentAsync(entityType, result, isNew: true, httpContext.RequestAborted);
            return ClientResult.Envelope(clientAccessor, presented, 201);
        }
        catch (SparkCancelException)
        {
            // An interceptor cancelled the create (#482): nothing was created, and nothing went wrong.
            return ClientResult.Envelope(clientAccessor, null, 204);
        }
        catch (SparkConcurrencyException ex)
        {
            // A creation whose natural id is already held by a row the caller may edit: "exists"
            // (#467, D16), never an overwrite. Generic body, as in Update (R2-M1).
            return ClientResult.ConcurrencyConflict(clientAccessor, ex);
        }
        catch (SparkSaveValidationException ex)
        {
            return ClientResult.Envelope(clientAccessor, new { errors = ex.Result.Errors }, 400);
        }
        catch (SparkValidationException ex)
        {
            return ClientResult.Envelope(clientAccessor, new { errors = new[] { ex.ToError() } }, 400);
        }
        catch (SparkActionDisabledException ex)
        {
            // After the row gate (#460, D13): the caller can see this row, so naming the action hides nothing.
            return ClientResult.ActionDisabled(clientAccessor, ex);
        }
        catch (SparkThrottledException ex)
        {
            // A business quota (Moderation's new-account throttle, #460 M12): 429, not 404 or 400.
            return ClientResult.Throttled(clientAccessor, httpContext, ex);
        }
        catch (SparkAccessDeniedException)
        {
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
        }
    }
}
