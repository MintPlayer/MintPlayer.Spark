using ExternalLoginErrors = MintPlayer.Spark.Authorization.Extensions.SparkAuthenticationExtensions.ExternalLoginErrors;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.Endpoints.ExternalLogin;

/// <summary>
/// Detaches an external login from the signed-in user, unless it is the last way in.
/// </summary>
[MemberOf<SparkAuthGroup>]
internal sealed partial class UnlinkExternalLogin<TUser> : IPostEndpoint
    where TUser : SparkUser, new()
{
    public static string Path => "/external-logins/unlink";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
    {
        builder
            .RequireAuthorization()
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true));
    }

    [Inject] private readonly UserManager<TUser> userManager;
    [Inject] private readonly SignInManager<TUser> signInManager;
    [Inject] private readonly IOptions<SparkAuthenticationOptions> options;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var provider = httpContext.Request.Query["provider"].ToString();
        var providerKey = httpContext.Request.Query["providerKey"].ToString();

        var user = await userManager.GetUserAsync(httpContext.User);
        if (user is null)
            return Results.Unauthorized();

        var logins = await userManager.GetLoginsAsync(user);
        if (!logins.Any(l =>
                string.Equals(l.LoginProvider, provider, StringComparison.Ordinal)
                && string.Equals(l.ProviderKey, providerKey, StringComparison.Ordinal)))
        {
            return Results.BadRequest(new { error = ExternalLoginErrors.LoginNotFound });
        }

        // ⚠️ The guard. Removing the last way in is permanent: no password to fall back on, no
        // provider left to prove ownership, and no self-service route back. Identity will happily
        // do it.
        //
        // A passkey is a credential too, so it counts toward "is this the last way in". Without
        // that, an account holding a working passkey would be refused permission to unlink its only
        // external login — the guard failing closed, but wrongly.
        if (SparkCredentialInventory.WouldRemoveLastCredential(
                logins.Count, await userManager.HasPasswordAsync(user), options.Value.LocalCredentials,
                (await userManager.GetPasskeysAsync(user)).Count, options.Value.Passkeys))
        {
            return Results.BadRequest(new { error = ExternalLoginErrors.LastCredential });
        }

        var result = await userManager.RemoveLoginAsync(user, provider, providerKey);
        if (!result.Succeeded)
            return Results.BadRequest(new { error = ExternalLoginErrors.UnlinkFailed });

        // The security stamp carries into the cookie, so refreshing it is what makes the removal
        // take effect on sessions other than this one.
        await signInManager.RefreshSignInAsync(user);
        return Results.Ok(new { unlinked = true });
    }
}
