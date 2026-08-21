using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;

namespace MintPlayer.Spark.Authorization.Extensions;

/// <summary>
/// Answers "which registered authentication schemes is a user actually able to click on?".
/// </summary>
/// <remarks>
/// The scheme table mixes three unrelated things: Identity's own internal schemes, non-interactive
/// credential schemes an application adds for machine callers (API tokens, JWT bearer, client
/// certificates), and interactive external providers. Only the third belongs on a sign-in page, and
/// only the third makes an application with local credentials disabled reachable. The distinguishing
/// mark is a <see cref="AuthenticationScheme.DisplayName"/> — <c>AddGitHub</c>, <c>AddGoogle</c> and
/// friends set one because it is meant to be shown to a human; Identity's internal schemes and the
/// bearer/certificate handlers do not.
/// </remarks>
internal static class ExternalAuthenticationSchemes
{
    private static readonly HashSet<string> IdentityOwnSchemes = new(StringComparer.Ordinal)
    {
        IdentityConstants.ApplicationScheme,
        IdentityConstants.ExternalScheme,
        IdentityConstants.BearerScheme,
        IdentityConstants.TwoFactorUserIdScheme,
        IdentityConstants.TwoFactorRememberMeScheme,
    };

    internal static async Task<IReadOnlyList<AuthenticationScheme>> GetInteractiveAsync(IServiceProvider services)
    {
        var provider = services.GetService(typeof(IAuthenticationSchemeProvider)) as IAuthenticationSchemeProvider;
        if (provider is null)
            return [];

        var schemes = await provider.GetAllSchemesAsync();
        return [.. schemes.Where(scheme =>
            !string.IsNullOrEmpty(scheme.DisplayName) && !IdentityOwnSchemes.Contains(scheme.Name))];
    }
}
