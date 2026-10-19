using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints;

/// <summary>
/// Endpoint filters that answer a <see cref="SparkAccessDeniedException"/> escaping the handler with
/// the uniform refusal (<see cref="SparkDenial"/>): 401 for an anonymous caller, 404 otherwise.
/// </summary>
/// <remarks>
/// <para>
/// These replace the <c>try { EnsureAuthorizedAsync(...) } catch (SparkAccessDeniedException) { return refusal; }</c>
/// block every access endpoint used to carry (#521 X20 item 9). A handler now calls
/// <c>EnsureAuthorizedAsync</c> and lets the denial propagate; the endpoint opts in once, in its
/// <c>Configure</c>:
/// <code>
/// static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
///     => builder.RefuseDeniedInEnvelope();
/// </code>
/// </para>
/// <para>
/// ⚠️ <b>Per endpoint, never per group.</b> A group filter would also cover endpoints that answer a
/// denial differently on purpose (<c>queries/distinct</c>'s 404 names the query), or not at all.
/// </para>
/// <para>
/// ⚠️ A handler with a catch-all (<c>catch (Exception)</c>) must exclude the denial from it
/// (<c>when (ex is not SparkAccessDeniedException)</c>), or the catch-all answers 500 before the
/// filter sees it.
/// </para>
/// <para>
/// The envelope variant resolves <see cref="IClientAccessor"/> from <c>RequestServices</c>: an endpoint
/// filter is built once per endpoint and has no request scope of its own to inject from. It is the
/// scoped instance the handler used, so operations already queued before the denial stay in the
/// envelope, exactly as they did when the handler refused.
/// </para>
/// </remarks>
public static class SparkDenialFilters
{
    /// <summary>
    /// Answers a denial in the client-operations envelope (<c>{ result, operations }</c>), as
    /// <c>/spark/po/*</c>, <c>/spark/actions/*</c> and the add-on endpoints (<c>ISparkAddOnEndpoints.Refusal</c>) do.
    /// </summary>
    public static TBuilder RefuseDeniedInEnvelope<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
        => builder.AddEndpointFilter(async (invocation, next) =>
        {
            try
            {
                return await next(invocation);
            }
            catch (SparkAccessDeniedException)
            {
                var httpContext = invocation.HttpContext;
                return ClientResult.EnvelopeRefusal(httpContext.RequestServices.GetRequiredService<IClientAccessor>(), httpContext);
            }
        });

    /// <summary>
    /// Answers a denial as a bare JSON body (<c>{ error }</c>), as <c>/spark/lookupref/*</c> does
    /// (<see cref="SparkDenial.RefuseJson"/>).
    /// </summary>
    public static TBuilder RefuseDeniedAsJson<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
        => builder.AddEndpointFilter(async (invocation, next) =>
        {
            try
            {
                return await next(invocation);
            }
            catch (SparkAccessDeniedException)
            {
                return SparkDenial.RefuseJson(invocation.HttpContext);
            }
        });
}
