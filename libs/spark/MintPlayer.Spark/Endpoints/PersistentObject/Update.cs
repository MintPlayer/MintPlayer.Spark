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
internal sealed partial class UpdatePersistentObject : IPostEndpoint
{
    public static string Path => "/update";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder)
    {
        builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));
    }

    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly ISaveValidation saveValidation;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IRetryAccessor retryAccessor;
    [Inject] private readonly IClientAccessor clientAccessor;
    [Inject] private readonly ISaveResponsePresenter saveResponse;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var (request, entityType) = await SparkRequestType.ReadAsync<PersistentObjectRequest>(httpContext, modelLoader);
        if (request is null || entityType is null || string.IsNullOrEmpty(request.Id))
        {
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
        }

        // A body without the object is malformed: refused like one, not a 500.
        if (request.PersistentObject is not { } obj)
        {
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
        }

        // An update says which version it edits (#467, D16): without one it would overwrite whatever
        // is stored now. Before any read, so the answer says nothing about the row.
        if (string.IsNullOrEmpty(obj.Etag))
        {
            return ClientResult.Envelope(clientAccessor, new { error = "An update must carry the etag of the version it edits." }, 400);
        }

        try
        {
            // No UnescapeDataString: the id is a JSON string now, not a path segment, so it arrives
            // exactly as the client wrote it. Unescaping it here would corrupt any id that legitimately
            // contains a '%'.
            var existingObj = await databaseAccess.GetPersistentObjectAsync(entityType.Id, request.Id);

            // The caller loaded this object (it holds an etag) and it is gone — deleted, soft-deleted,
            // or no longer theirs to see: "deleted by another user" (#467, D15), never a resurrection.
            // One answer for all three, so it tells nothing a 404 would not (M-3).
            if (existingObj is null)
            {
                return SparkAddOnEndpoints.ConcurrencyConflict(clientAccessor, SparkConcurrencyException.DeletedSinceLoaded(obj.Etag));
            }

            RetryScope.Accept(retryAccessor, request);

            obj.Id = existingObj.Id;
            obj.ObjectTypeId = entityType.Id;
            // Only a create resolves a parent (from a sub-query's New); a Parent in this body is the
            // caller's claim and must never reach a save hook as context.
            obj.Parent = null;

            // Authorize before validating — see the note in Create.cs (N23).
            await databaseAccess.EnsureSaveAuthorizedAsync(obj);

            // Validated inside the save, right after the write shield (contributions M2d): against the
            // object as the refresh hook shapes it, not as the model declares it (a hook that makes a
            // field required changed the contract, and re-deriving it server-side is what stops a
            // client escaping the hook by never calling /refresh) — and only on the attributes the
            // caller may write, since the others keep their stored value whatever was posted.
            saveValidation.Request(obj, httpContext.RequestAborted);

            var result = await databaseAccess.SavePersistentObjectAsync(obj);

            // Re-presented as a load presents it (contributions M2c-2b, leak 2): never the posted
            // object, which is the client's values plus whatever the save hooks wrote into it.
            var presented = await saveResponse.PresentAsync(entityType, result, isNew: false, httpContext.RequestAborted);
            return ClientResult.Envelope(clientAccessor, presented, 200);
        }
        catch (SparkConcurrencyException ex)
        {
            // R2-M1: SparkConcurrencyException.Message contains the server-side
            // change vector — useful for the legitimate optimistic-concurrency
            // recovery flow, but it leaks document-version state that an
            // attacker can use as a side channel. Return a generic 409 that says only
            // whether the row changed or went; clients re-fetch on 409.
            return SparkAddOnEndpoints.ConcurrencyConflict(clientAccessor, ex);
        }
        catch (SparkSaveValidationException ex)
        {
            return ClientResult.Envelope(clientAccessor, new { errors = ex.Result.Errors }, 400);
        }
        catch (SparkValidationException ex)
        {
            return ClientResult.Envelope(clientAccessor, new { errors = new[] { ex.ToError() } }, 400);
        }
        catch (SparkRowLevelAccessDeniedException)
        {
            // R2-H2: row-level denial returns 404 to match the read path —
            // M-3 says authorized-but-forbidden must be indistinguishable from
            // not-found for instance-level checks.
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
        }
        catch (SparkActionDisabledException ex)
        {
            // After the row gate (#460, D13): the caller can see this row, so naming the action hides nothing.
            return ClientResult.ActionDisabled(clientAccessor, ex);
        }
        catch (SparkAccessDeniedException)
        {
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
        }
    }
}
