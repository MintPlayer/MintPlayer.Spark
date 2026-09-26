using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.Endpoints.ExternalLogin;

/// <summary>
/// The other half of <c>ConfirmByEmail</c>: completes a link from a token mailed to the account's
/// address.
/// </summary>
/// <remarks>
/// ⚠️ Reached from a link in a mailbox, so it is a plain top-level GET — there is no popup to post
/// back to and no session to carry an antiforgery token. The single-use token <em>is</em> the
/// credential; that is what a confirmation link is.
/// </remarks>
[MemberOf<SparkAuthGroup>]
internal sealed partial class ConfirmExternalLink<TUser> : IGetEndpoint
    where TUser : SparkUser, new()
{
    public static string Path => "/confirm-external-link";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder)
    {
        builder.AllowAnonymous();
    }

    [Inject] private readonly SignInManager<TUser> signInManager;
    [Inject] private readonly UserManager<TUser> userManager;
    [Inject] private readonly SparkExternalLoginLinker<TUser> linker;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var token = httpContext.Request.Query["token"].ToString();
        var returnUrl = httpContext.Request.Query["returnUrl"].ToString();

        var safeReturnUrl = SparkAuthenticationExtensions.SanitizeReturnUrl(
            string.IsNullOrEmpty(returnUrl) ? null : returnUrl);

        // Usually absent: the reader is in their mailbox, not mid-OAuth. When it *is* present and
        // names a different identity than the confirmation was issued for, that is the substitution
        // the whole design is built against, and the linker refuses it.
        var ambient = await signInManager.GetExternalLoginInfoAsync();
        var result = await linker.ConfirmAsync(
            string.IsNullOrEmpty(token) ? null : token,
            ambient?.ProviderKey,
            httpContext.RequestAborted);

        if (result.Outcome != SparkLinkConfirmationOutcome.Linked)
        {
            return Results.Redirect(QueryHelpers.AddQueryString(
                safeReturnUrl, "sparkLinkConfirmation", SparkAuthenticationExtensions.ConfirmationCode(result.Outcome)));
        }

        // Signing in here is the point: the person proved control of the account's mailbox, and
        // sending them back to a sign-in page after that would ask them to prove it twice.
        var user = await userManager.FindByIdAsync(result.UserId!);
        if (user is not null)
            await signInManager.SignInAsync(user, isPersistent: true);

        return Results.Redirect(QueryHelpers.AddQueryString(
            safeReturnUrl, "sparkLinkConfirmation", "linked"));
    }
}
