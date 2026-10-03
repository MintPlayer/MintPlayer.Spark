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
internal sealed partial class DeletePersistentObject : IPostEndpoint
{
    public static string Path => "/delete";

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
        // A delete always carries a body now, so the conditional read this used to do — "DELETE may
        // carry JSON on retry resubmission", sniffed off Content-Type — is gone with the verb.
        var (request, entityType) = await SparkRequestType.ReadAsync<PersistentObjectReferenceRequest>(httpContext, modelLoader);
        if (request is null || entityType is null || string.IsNullOrEmpty(request.Id))
        {
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
        }

        RetryScope.Accept(retryAccessor, request);

        try
        {
            // No UnescapeDataString: see the note in Update.
            var obj = await databaseAccess.GetPersistentObjectAsync(entityType.Id, request.Id);

            if (obj is null)
            {
                return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
            }

            await databaseAccess.DeletePersistentObjectAsync(entityType.Id, request.Id);
            return ClientResult.Envelope(clientAccessor, null, 204);
        }
        catch (SparkConcurrencyException)
        {
            // A replaced (soft) delete is a write, and it met a concurrent edit (contributions F7).
            // Generic body, as in Update (R2-M1): the exception carries change vectors.
            return SparkAddOnEndpoints.ConcurrencyConflict(clientAccessor);
        }
        catch (SparkValidationException ex)
        {
            // A delete can be refused for a business reason too — "this client still has live
            // tokens", say. Same envelope, so the screen shows it the same way.
            return ClientResult.Envelope(clientAccessor, new { errors = new[] { ex.ToError() } }, 400);
        }
        catch (SparkRowLevelAccessDeniedException)
        {
            // R2-H2: row-level Delete denial returns 404 (M-3 uniformity).
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
        catch (SparkThrottledException ex)
        {
            // A business quota from a hook (#460, M12), answered as delete-many answers it (#467, D21).
            return ClientResult.Throttled(clientAccessor, httpContext, ex);
        }
    }
}
