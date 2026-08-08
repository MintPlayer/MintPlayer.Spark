using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;

namespace MintPlayer.Spark.IdentityProvider.Endpoints;

internal static class InteractiveUserExtensions
{
    /// <summary>
    /// The id of the end user driving an interactive OIDC page, or null if nobody is.
    /// <para>
    /// Resolved explicitly against <see cref="IdentityConstants.ApplicationScheme"/> rather
    /// than read off ambient <see cref="HttpContext.User"/>. Under
    /// <c>AddIdentityApiEndpoints</c> the ambient principal is whatever the *first* registered
    /// scheme produces, and that is the bearer scheme — so a Spark API access token satisfied
    /// "is a user signed in?" on <c>/connect/authorize</c> and the consent pages. A
    /// non-interactive credential could therefore drive the whole interactive grant headlessly
    /// and mint authorization codes with no human at any screen, which is the one thing this
    /// flow exists to guarantee.
    /// </para>
    /// <para>
    /// The authorization-code grant delegates <em>a person's</em> authority. Only the cookie
    /// the login page issues evidences a person, so only that scheme is consulted here.
    /// </para>
    /// </summary>
    public static async Task<string?> GetInteractiveUserIdAsync(this HttpContext context)
    {
        var result = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (!result.Succeeded)
            return null;

        var userId = result.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        return string.IsNullOrEmpty(userId) ? null : userId;
    }
}
