using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.Endpoints.ExternalLogin;

/// <summary>
/// Starts an OAuth challenge that will attach a provider to the <em>already signed-in</em> user.
/// </summary>
[MemberOf<SparkAuthGroup>]
internal sealed partial class LinkExternalLoginChallenge<TUser> : IGetEndpoint
    where TUser : SparkUser, new()
{
    public static string Path => "/external-logins/link";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
    {
        builder.RequireAuthorization();
    }

    [Inject] private readonly SignInManager<TUser> signInManager;

    public Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var provider = httpContext.Request.Query["provider"].ToString();
        var returnUrl = httpContext.Request.Query["returnUrl"].ToString();
        var popup = httpContext.Request.Query["popup"].ToString();

        var safeReturnUrl = SparkAuthenticationExtensions.SanitizeReturnUrl(
            string.IsNullOrEmpty(returnUrl) ? null : returnUrl);
        var callbackUrl = $"/spark/auth/link-external-login-callback?returnUrl={Uri.EscapeDataString(safeReturnUrl)}";
        if (!string.IsNullOrEmpty(popup))
            callbackUrl += "&popup=1";

        // ⚠️ Keyed on the signed-in user so that the identity coming back is attached to the session
        // that asked, not to whoever the callback happens to find signed in. Identity uses it to
        // reject a callback that lands in a different session.
        var properties = signInManager.ConfigureExternalAuthenticationProperties(
            provider, callbackUrl, userId: httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier));
        return Task.FromResult(Results.Challenge(properties, [provider]));
    }
}
