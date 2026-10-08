using ExternalLoginErrors = MintPlayer.Spark.Authorization.Extensions.SparkAuthenticationExtensions.ExternalLoginErrors;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.Endpoints.ExternalLogin;

/// <summary>
/// Completes a link ceremony started by <see cref="LinkExternalLoginChallenge{TUser}"/>.
/// </summary>
[MemberOf<SparkAuthGroup>]
internal sealed partial class LinkExternalLoginCallback<TUser> : IGetEndpoint
    where TUser : SparkUser, new()
{
    public static string Path => "/link-external-login-callback";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
    {
        builder.RequireAuthorization();
    }

    [Inject] private readonly SignInManager<TUser> signInManager;
    [Inject] private readonly UserManager<TUser> userManager;
    [Inject] private readonly IAntiforgery antiforgery;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var returnUrl = httpContext.Request.Query["returnUrl"].ToString();
        var safeReturnUrl = SparkAuthenticationExtensions.SanitizeReturnUrl(
            string.IsNullOrEmpty(returnUrl) ? null : returnUrl);

        var user = await userManager.GetUserAsync(httpContext.User);
        if (user is null)
            return SparkAuthenticationExtensions.ExternalLoginOutcome(httpContext, safeReturnUrl, ExternalLoginErrors.SignInToLink);

        var info = await signInManager.GetExternalLoginInfoAsync(
            await userManager.GetUserIdAsync(user));
        if (info is null)
            return SparkAuthenticationExtensions.ExternalLoginOutcome(httpContext, safeReturnUrl, ExternalLoginErrors.NoLoginInfo);

        var result = await userManager.AddLoginAsync(user, info);
        if (!result.Succeeded)
        {
            // Told apart deliberately. "Already attached somewhere" is a fact about the world the
            // user can act on — sign in with it, or detach it there first — while a store failure is
            // not, and reporting them the same way sends people looking for the wrong problem.
            var error = result.Errors.Any(e => e.Code == "LoginAlreadyAssociated")
                ? ExternalLoginErrors.LoginAlreadyAssociated
                : ExternalLoginErrors.LinkFailed;
            return SparkAuthenticationExtensions.ExternalLoginOutcome(httpContext, safeReturnUrl, error);
        }

        // Nothing about the session changed except which credentials reach it, and the external
        // cookie is spent; refreshing keeps this consistent with the unlink path.
        await signInManager.RefreshSignInAsync(user);
        antiforgery.GetAndStoreTokens(httpContext);
        return SparkAuthenticationExtensions.ExternalLoginOutcome(httpContext, safeReturnUrl, error: null);
    }
}
