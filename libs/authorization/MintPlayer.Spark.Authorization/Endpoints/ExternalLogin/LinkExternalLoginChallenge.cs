using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Configuration;
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

    /// <summary>
    /// <see cref="SparkExternalLoginLinking.WhenSignedIn"/> only: holding the session is that mode's
    /// proof. <see cref="SparkExternalLoginLinking.ConfirmByEmail"/> attaches through the mailed
    /// confirmation instead.
    /// </summary>
    static bool IEndpointBase.IsEnabled(IServiceProvider services)
        => SparkAuthFeatures.ExternalLoginLinking(services) == SparkExternalLoginLinking.WhenSignedIn;

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
    {
        builder.RequireAuthorization();
    }

    [QueryParam] public string? Provider { get; set; }
    [QueryParam] public string? ReturnUrl { get; set; }
    [QueryParam] public string? Popup { get; set; }
    [QueryParam] public string? Nonce { get; set; }

    [Inject] private readonly SignInManager<TUser> signInManager;

    public Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var provider = Provider ?? string.Empty;
        var returnUrl = ReturnUrl;
        var popup = Popup;

        // #490 D1: same nonce rule as the sign-in challenge.
        if (SparkExternalLoginNonce.Reject(Nonce) is { } invalidNonce)
            return Task.FromResult(invalidNonce);

        var safeReturnUrl = SparkAuthenticationExtensions.SanitizeReturnUrl(
            string.IsNullOrEmpty(returnUrl) ? null : returnUrl);
        var callbackUrl = SparkExternalLoginNonce.AppendCallbackFlags(
            $"/spark/auth/link-external-login-callback?returnUrl={Uri.EscapeDataString(safeReturnUrl)}",
            popup,
            Nonce);

        // ⚠️ Keyed on the signed-in user so that the identity coming back is attached to the session
        // that asked, not to whoever the callback happens to find signed in. Identity uses it to
        // reject a callback that lands in a different session.
        var properties = signInManager.ConfigureExternalAuthenticationProperties(
            provider, callbackUrl, userId: httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier));
        return Task.FromResult(Results.Challenge(properties, [provider]));
    }
}
