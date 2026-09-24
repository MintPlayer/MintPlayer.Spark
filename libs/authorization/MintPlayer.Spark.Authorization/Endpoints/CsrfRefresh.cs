using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;

namespace MintPlayer.Spark.Authorization.Endpoints;

/// <summary>
/// Mints a fresh <c>XSRF-TOKEN</c> bound to the caller's <em>current</em> identity. The handler does
/// nothing; the cookie is written by the mint in <c>UseSpark()</c> that runs on every response. The
/// endpoint exists so the client has something cheap to call at the moment its identity changes.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>This endpoint must never require an antiforgery token, and the exemption is explicit rather
/// than inherited.</b> An antiforgery token is bound to the principal it was minted for, so the
/// moment a sign-in or sign-out changes that principal the client's token is stale by definition —
/// and asking a client holding a stale token to present a valid one, in order to be given a valid
/// one, is a deadlock with no way out. A browser in that state cannot make another mutating call for
/// the rest of the session.
/// </para>
/// <para>
/// It is marked rather than merely left unmarked because Spark's gate defaults to requiring
/// antiforgery for any mutating request under <c>/spark</c> that carries an ambient credential
/// (<c>SparkAntiforgeryOptions.RequireAntiforgery</c>, true since 11.0.0). This endpoint is a POST
/// under <c>/spark</c> and the caller is cookie-authenticated on exactly the path that matters —
/// after sign-in — so the inverted default would otherwise catch it. Explicit metadata wins in both
/// directions, which is what makes this safe to state once here instead of hoping no default ever
/// reaches it.
/// </para>
/// <para>
/// Exempting it costs nothing: the endpoint reads no input, writes no state, and returns an empty
/// 200. Forging a request to it achieves precisely one thing — giving the victim's own browser a
/// fresh cookie it was entitled to anyway.
/// </para>
/// </remarks>
internal sealed class CsrfRefresh : IPostEndpoint, IMemberOf<SparkAuthGroup>, IEndpointBase
{
    public static string Path => "/csrf-refresh";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder)
    {
        builder.WithMetadata(new RequireAntiforgeryTokenAttribute(false));
    }

    public Task<IResult> HandleAsync(HttpContext httpContext)
    {
        return Task.FromResult(Results.Ok());
    }
}
