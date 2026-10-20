using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Exceptions;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.Actions;

[MemberOf<ActionsGroup>]
internal sealed partial class ExecuteCustomAction : IPostEndpoint<CustomActionRequest>
{
    public static string Path => "/execute";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
    {
        builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));
        builder.RefuseDeniedInEnvelope();
    }

    [Inject] private readonly IModelLoader modelLoader;
    // Every gate between the request and the action — grant, catalogue, selection, row checks,
    // disabled actions, quotas — lives here, shared with the MCP tools (MCP PRD D9). This endpoint
    // only reads the body and maps the outcome to HTTP.
    [Inject] private readonly ICustomActionExecution execution;
    [Inject] private readonly IClientAccessor clientAccessor;
    [Inject] private readonly ILogger<ExecuteCustomAction> logger;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    /// <summary>A body that cannot be bound gets the refusal an unusable request gets below, never a parse error (PRD D3a).</summary>
    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => new(ClientResult.EnvelopeRefusal(clientAccessor, context));

    public override async Task<IResult> HandleAsync(CustomActionRequest request, CancellationToken cancellationToken)
    {
        var httpContext = httpContextAccessor.HttpContext!;
        // Both the type and the action name arrive in the body now, so the body is read first. The
        // ordering the old code relied on — authorize, then read — is preserved in effect because a
        // malformed body is refused here in exactly the shape an unknown type is refused below.
        var entityType = SparkRequestType.Resolve(modelLoader, request);
        var actionName = request.ActionName;

        if (entityType is null || string.IsNullOrEmpty(actionName))
        {
            // Same shape as a denial. This ran BEFORE the grant check, so a specific
            // 404 here against a 401 there told an anonymous caller which entity types are
            // real -- the M-3 oracle, in the one endpoint the sweep missed.
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
        }

        try
        {
            var outcome = await execution.ExecuteAsync(entityType, new CustomActionInvocation(
                actionName,
                request.Parent,
                request.SelectedItemIds,
                request.ParentId,
                request.ParentType,
                request.QueryId,
                request), httpContext.RequestAborted);

            return outcome.Kind switch
            {
                // T5 (#460): whatever the action handed to SetResult, as the envelope's result. Null when
                // it set nothing, which is the shape every existing caller already reads.
                CustomActionOutcomeKind.Executed => ClientResult.Envelope(clientAccessor, outcome.Result, StatusCodes.Status200OK),
                CustomActionOutcomeKind.NotFound => ClientResult.Envelope(clientAccessor, new { error = outcome.Error }, StatusCodes.Status404NotFound),
                CustomActionOutcomeKind.Invalid => ClientResult.Envelope(clientAccessor, new { error = outcome.Error }, StatusCodes.Status400BadRequest),
                CustomActionOutcomeKind.Disabled => ClientResult.ActionDisabled(clientAccessor, new SparkActionDisabledException(actionName)),
                _ => ClientResult.EnvelopeRefusal(clientAccessor, httpContext),
            };
        }
        catch (SparkActionDisabledException ex)
        {
            // Raised by a write the action made through IDatabaseAccess, on an object whose own hook
            // disables that write. A 403 naming it, rather than the catch-all's anonymous 500.
            return ClientResult.ActionDisabled(clientAccessor, ex);
        }
        catch (SparkValidationException ex)
        {
            // A write the action made through IDatabaseAccess that an interceptor refused for a
            // readable reason (a locked post, #460 M12 spike S-MOD-D): a 400 like /po/update gives,
            // not the catch-all's anonymous 500.
            return ClientResult.Envelope(clientAccessor, new { errors = new[] { ex.ToError() } }, StatusCodes.Status400BadRequest);
        }
        catch (SparkThrottledException ex)
        {
            return ClientResult.Throttled(clientAccessor, httpContext, ex);
        }
        // ⚠️ The filter is load-bearing, and this is the only endpoint that needs one. A retry is not
        // a failure — it is the server asking the caller a question, and the middleware turns it into
        // a 449. Every other retry-capable endpoint catches only specific exception types, so the
        // exception simply propagates; this one has a catch-all, which would swallow the prompt and
        // answer 500 instead. `RetryFromEveryHookTests` is what fails if this filter is removed.
        // A denial passes through the same way, to the endpoint filter's refusal (Configure).
        catch (Exception ex) when (ex is not (SparkRetryActionException or SparkAccessDeniedException))
        {
            // R2-M1: server-side log with full detail, generic public response.
            logger.LogError(ex, "Custom action '{ActionName}' failed for entity type '{EntityType}'", actionName, entityType.Name);
            return ClientResult.Envelope(clientAccessor, new { error = "Operation failed" }, StatusCodes.Status500InternalServerError);
        }
    }
}
