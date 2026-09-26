using ExternalLoginErrors = MintPlayer.Spark.Authorization.Extensions.SparkAuthenticationExtensions.ExternalLoginErrors;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.Endpoints.ExternalLogin;

/// <summary>
/// Initiates the OAuth challenge for an external provider.
/// </summary>
[MemberOf<SparkAuthGroup>]
internal sealed partial class ExternalLoginChallenge<TUser> : IGetEndpoint
    where TUser : SparkUser, new()
{
    public static string Path => "/external-login";

    [Inject] private readonly SignInManager<TUser> signInManager;
    [Inject] private readonly IAuthenticationSchemeProvider schemes;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var provider = httpContext.Request.Query["provider"].ToString();
        var returnUrl = httpContext.Request.Query["returnUrl"].ToString();
        var popup = httpContext.Request.Query["popup"].ToString();

        if (string.IsNullOrEmpty(provider))
            return Results.BadRequest(new { error = ExternalLoginErrors.UnknownProvider });

        // R2-M3: validate returnUrl at the entry point. Even if R2-C4's callback fix were bypassed,
        // accepting an absolute attacker URL here round-trips through OAuth state and reflects back
        // to the callback unchanged. Substitute the default for anything non-local.
        var safeReturnUrl = SparkAuthenticationExtensions.SanitizeReturnUrl(
            string.IsNullOrEmpty(returnUrl) ? null : returnUrl);
        var callbackUrl = $"/spark/auth/external-login-callback?returnUrl={Uri.EscapeDataString(safeReturnUrl)}";

        // The callback is a fresh top-level navigation, so the only thing that survives this hop is
        // the URL — carrying the popup flag forward here is what makes the callback's postMessage
        // branch reachable at all. Plain query string, not OAuth `state`: this URL never reaches the
        // provider (ASP.NET encrypts it into `state` itself as AuthenticationProperties.RedirectUri,
        // and the provider only ever sees the registered CallbackPath).
        if (!string.IsNullOrEmpty(popup))
            callbackUrl += "&popup=1";

        // 4h: an unregistered scheme reaches Results.Challenge and throws, so an unknown ?provider=
        // answered with a 500 and a stack trace in the log. It is a bad request — most often a
        // client and a deployment disagreeing about which providers exist, which a 500 actively
        // hides.
        if (await schemes.GetSchemeAsync(provider) is null)
            return Results.BadRequest(new { error = ExternalLoginErrors.UnknownProvider });

        var properties = signInManager.ConfigureExternalAuthenticationProperties(provider, callbackUrl);
        return Results.Challenge(properties, [provider]);
    }
}
