using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using MintPlayer.Spark.Authorization.Identity;

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
        => (await context.GetInteractiveUserAsync()).UserId;

    /// <summary>
    /// <see cref="GetInteractiveUserIdAsync"/> together with when that person signed in, for the
    /// id_token's <c>auth_time</c>.
    /// </summary>
    /// <remarks>
    /// The instant is the one <see cref="SparkSignInManager{TUser}"/> stamps on every sign-in and keeps
    /// across cookie refreshes (<see cref="SparkSignInManager{TUser}.AuthenticatedAtItem"/>); a ticket
    /// without it (issued by a plain <c>SignInManager</c>) falls back to the ticket's issue time, which a
    /// sliding refresh can move later but never earlier than the real sign-in.
    /// </remarks>
    /// <summary>The signed-in person's user name (the cookie's Name claim), for "signed in as" on the consent page (D6).</summary>
    public static async Task<string?> GetInteractiveUserNameAsync(this HttpContext context)
    {
        var result = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        return result.Succeeded ? result.Principal?.Identity?.Name : null;
    }

    public static async Task<(string? UserId, DateTimeOffset? AuthTime)> GetInteractiveUserAsync(this HttpContext context)
    {
        var result = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (!result.Succeeded)
            return (null, null);

        var userId = result.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return (null, null);

        DateTimeOffset? authTime = null;
        if (result.Properties?.Items.TryGetValue(SparkSignInManager<SparkUser>.AuthenticatedAtItem, out var raw) == true
            && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
        {
            authTime = at;
        }
        else
        {
            authTime = result.Properties?.IssuedUtc;
        }

        return (userId, authTime);
    }
}
