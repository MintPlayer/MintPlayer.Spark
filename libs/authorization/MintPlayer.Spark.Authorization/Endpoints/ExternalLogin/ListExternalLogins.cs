using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.Endpoints.ExternalLogin;

/// <summary>
/// The signed-in user's linked external logins, and the providers still available to link.
/// </summary>
/// <remarks>
/// ⚠️ <c>LocalCredentials</c> and <c>Passkeys</c> are read from <see cref="IOptions{T}"/> here. They
/// used to be closed over from <c>MapSparkIdentityApi</c>'s parameters, which an endpoint class
/// cannot see. Production passed <c>options.Value.LocalCredentials</c> into that parameter anyway,
/// so this is the same value by a shorter route — but a caller that passed something different from
/// what it configured would now see the configured value.
/// </remarks>
[MemberOf<SparkAuthGroup>]
internal sealed partial class ListExternalLogins<TUser> : IGetEndpoint
    where TUser : SparkUser, new()
{
    public static string Path => "/external-logins";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder)
    {
        builder.RequireAuthorization();
    }

    [Inject] private readonly UserManager<TUser> userManager;
    [Inject] private readonly IAuthenticationSchemeProvider schemes;
    [Inject] private readonly IOptions<SparkAuthenticationOptions> options;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var user = await userManager.GetUserAsync(httpContext.User);
        if (user is null)
            return Results.Unauthorized();

        var logins = await userManager.GetLoginsAsync(user);
        var hasPassword = await userManager.HasPasswordAsync(user);

        // Computed per login rather than once for the account, because it is the answer to "can I
        // remove *this* one" — and it is served to the client so the UI can disable the button
        // instead of offering an action that will be refused.
        var canUnlink = !SparkCredentialInventory.WouldRemoveLastCredential(
            logins.Count, hasPassword, options.Value.LocalCredentials,
            (await userManager.GetPasskeysAsync(user)).Count, options.Value.Passkeys);

        var external = await schemes.GetAllSchemesAsync();
        var linked = logins.Select(l => l.LoginProvider).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return Results.Ok(new
        {
            linked = logins.Select(l => new
            {
                provider = l.LoginProvider,
                providerKey = l.ProviderKey,
                displayName = l.ProviderDisplayName ?? l.LoginProvider,
                canUnlink,
            }),
            available = external
                .Where(scheme => scheme.DisplayName is not null && !linked.Contains(scheme.Name))
                .Select(scheme => new { provider = scheme.Name, displayName = scheme.DisplayName }),
        });
    }
}
