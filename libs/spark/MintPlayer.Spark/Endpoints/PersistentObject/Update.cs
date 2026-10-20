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
internal sealed partial class UpdatePersistentObject : IPostEndpoint<PersistentObjectRequest>
{
    public static string Path => "/update";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
    {
        builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));
        // Type- and row-level denials. R2-H2: a row-level denial is the same 404 as the read path's
        // (M-3: authorized-but-forbidden is indistinguishable from not-found).
        builder.RefuseDeniedInEnvelope();
    }

    [Inject] private readonly IPersistentObjectSavePath savePath;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IClientAccessor clientAccessor;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    /// <summary>A body that cannot be bound gets the refusal an unusable request gets below, never a parse error (PRD D3a).</summary>
    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => new(ClientResult.EnvelopeRefusal(clientAccessor, context));

    public override async Task<IResult> HandleAsync(PersistentObjectRequest request, CancellationToken cancellationToken)
    {
        var httpContext = httpContextAccessor.HttpContext!;
        var entityType = SparkRequestType.Resolve(modelLoader, request);
        if (entityType is null || string.IsNullOrEmpty(request.Id))
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
            // Load, authorize, validate, save and present: the shared sequence the MCP tools run too (MCP
            // PRD D9). A row that is gone — deleted, soft-deleted, or no longer the caller's to see —
            // is "deleted by another user" (#467, D15), raised as the concurrency conflict below.
            // 200 with the row (as stored when an interceptor cancelled the save, #482); 202 when an
            // ISaveReplacement diverted it for approval (X7).
            var outcome = await savePath.UpdateAsync(entityType, request.Id, obj, request, httpContext.RequestAborted);
            return ClientResult.Saved(clientAccessor, outcome, 200);
        }
        catch (Abstractions.SparkConflictException ex)
        {
            // R2-M1: SparkConcurrencyException.Message contains the server-side
            // change vector — useful for the legitimate optimistic-concurrency
            // recovery flow, but it leaks document-version state that an
            // attacker can use as a side channel. Return a generic 409 that says only
            // whether the row changed or went; clients re-fetch on 409.
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
    }
}
