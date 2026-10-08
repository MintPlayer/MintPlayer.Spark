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
internal sealed partial class DeletePersistentObject : IPostEndpoint<PersistentObjectReferenceRequest>
{
    public static string Path => "/delete";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
    {
        builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));
    }

    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IRetryAccessor retryAccessor;
    [Inject] private readonly IClientAccessor clientAccessor;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    /// <summary>A body that cannot be bound gets the refusal an unusable request gets below, never a parse error (PRD D3a).</summary>
    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => new(ClientResult.EnvelopeRefusal(clientAccessor, context));

    public override async Task<IResult> HandleAsync(PersistentObjectReferenceRequest request, CancellationToken cancellationToken)
    {
        var httpContext = httpContextAccessor.HttpContext!;
        // A delete always carries a body now, so the conditional read this used to do — "DELETE may
        // carry JSON on retry resubmission", sniffed off Content-Type — is gone with the verb.
        var entityType = SparkRequestType.Resolve(modelLoader, request);
        if (entityType is null || string.IsNullOrEmpty(request.Id))
        {
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
        }

        // A delete says which version it removes (#467, D14), so a row edited since it was shown is a
        // 409 rather than lost. Before any read, so the answer says nothing about the row.
        if (string.IsNullOrEmpty(request.Etag))
        {
            return ClientResult.Envelope(clientAccessor, new { error = "A delete must carry the etag of the version it removes." }, 400);
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

            await databaseAccess.DeletePersistentObjectAsync(
                entityType.Id, request.Id, Abstractions.Interceptors.PersistentObjectOperation.Delete, request.Etag);
            return ClientResult.Envelope(clientAccessor, null, 204);
        }
        catch (SparkCancelException)
        {
            // An interceptor cancelled the delete (#482): nothing was deleted, and nothing went wrong.
            return ClientResult.Envelope(clientAccessor, null, 204);
        }
        catch (SparkConcurrencyException ex)
        {
            // The row changed since the caller saw it (#467, D14), caught by the etag check or by the
            // write itself (contributions F7). Generic body, as in Update (R2-M1): the exception carries
            // change vectors.
            return ClientResult.ConcurrencyConflict(clientAccessor, ex);
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
            // A business quota from an interceptor (#460, M12), answered as delete-many answers it (#467, D21).
            return ClientResult.Throttled(clientAccessor, httpContext, ex);
        }
    }
}
