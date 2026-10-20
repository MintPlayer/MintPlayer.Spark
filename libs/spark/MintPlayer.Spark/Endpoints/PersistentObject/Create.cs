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
        builder.RefuseDeniedInEnvelope();
    }

    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IPersistentObjectSavePath savePath;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IRetryAccessor retryAccessor;
    [Inject] private readonly IClientAccessor clientAccessor;
    [Inject] private readonly IPermissionService permissionService;
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
        // permission service memoises per request. A denial is answered by the endpoint filter.
        await permissionService.EnsureAuthorizedAsync("New", entityType.Name);

        // A body without the object is malformed: refused like one, not a 500.
        if (request.PersistentObject is not { } obj)
        {
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
        }

        // Set up retry state if this is a re-invocation
        RetryScope.Accept(retryAccessor, request);

        // The parent a sub-query card's New was started from: resolved and authorized exactly as
        // /po/new resolved it, then handed to the save hooks as obj.Parent. A Parent in the body is the
        // caller's claim, so it is replaced either way — never the context a hook decides on.
        var (resolved, refused) = await SubQueryNewParent.ResolveAsync(
            modelLoader, queryLoader, databaseAccess, entityType, request.ParentId, request.ParentType, request.QueryId);
        if (refused)
        {
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
        }

        try
        {
            // Authorize, validate, save and present: the shared sequence the MCP tools run too (MCP PRD
            // D9). It forces the id to null and the type to the resolved one (R2-M18).
            var outcome = await savePath.CreateAsync(entityType, obj, resolved?.Parent, httpContext.RequestAborted);

            // 201 created; 204 when an interceptor cancelled it (#482: nothing was created, and nothing went
            // wrong); 202 when an ISaveReplacement diverted it for approval (X7).
            return ClientResult.Saved(clientAccessor, outcome, 201);
        }
        catch (Abstractions.SparkConflictException ex)
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
    }
}
