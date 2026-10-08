using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>The person signed in at the provider, and how (<c>docs/identity_provider_platform_PRD.md</c> D8).</summary>
/// <param name="Amr">RFC 8176 authentication method references: <c>pwd</c>, <c>otp</c>, <c>mfa</c>, <c>hwk</c> (passkey), <c>fed</c> (external login).</param>
/// <param name="SessionId">The provider session id (<c>sid</c>), for logout.</param>
internal sealed record OidcInteractiveSession(string UserId, string? UserName, DateTimeOffset? AuthTime, IReadOnlyList<string> Amr, string? SessionId)
{
    /// <summary>The level of assurance (<c>acr</c>): two factors, or one.</summary>
    public string Acr => Amr.Any(a => a is "mfa" or "otp" or "hwk") ? OidcAcr.MultiFactor : OidcAcr.SingleFactor;

    /// <summary>
    /// Reads the provider's own sign-in cookie. The <c>amr</c> claims are what ASP.NET Identity's
    /// sign-in manager stamps (<c>pwd</c>, <c>mfa</c>); a sign-in through an external provider
    /// carries that provider as its authentication method and becomes <c>fed</c>.
    /// </summary>
    public static async Task<OidcInteractiveSession?> ReadAsync(HttpContext context)
    {
        var result = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (!result.Succeeded || result.Principal is not { } principal)
            return null;

        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return null;

        DateTimeOffset? authTime = result.Properties?.IssuedUtc;
        if (result.Properties?.Items.TryGetValue(SparkSignInManager<SparkUser>.AuthenticatedAtItem, out var raw) == true
            && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
        {
            authTime = at;
        }

        var amr = principal.FindAll("amr").Select(c => c.Value).ToList();
        var method = principal.FindFirstValue(ClaimTypes.AuthenticationMethod);
        if (!string.IsNullOrEmpty(method))
        {
            if (method.Contains("passkey", StringComparison.OrdinalIgnoreCase)) amr.Add("hwk");
            else if (!method.Equals("pwd", StringComparison.OrdinalIgnoreCase)) amr.Add("fed");
        }
        if (amr.Count == 0)
            amr.Add("pwd");

        return new OidcInteractiveSession(userId, principal.Identity?.Name, authTime, amr.Distinct().ToList(),
            principal.FindFirstValue(OidcSessions.SessionIdClaim));
    }
}

/// <summary>The <c>acr</c> values this provider asserts (<c>acr_values_supported</c>).</summary>
public static class OidcAcr
{
    /// <summary>One factor: a password, or an external provider.</summary>
    public const string SingleFactor = "urn:mintplayer:spark:acr:1fa";
    /// <summary>Two factors: a password and a one-time code, or a passkey.</summary>
    public const string MultiFactor = "urn:mintplayer:spark:acr:mfa";

    public static readonly string[] Supported = [SingleFactor, MultiFactor];

    /// <summary>Whether <paramref name="actual"/> satisfies the requested <paramref name="requested"/> (space-separated, any of them).</summary>
    public static bool Satisfies(string actual, string? requested)
    {
        var wanted = (requested ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (wanted.Length == 0) return true;
        return wanted.Any(w => w == actual || (w == SingleFactor && actual == MultiFactor));
    }
}

/// <summary>Provider sessions (<c>sid</c>), for back- and front-channel logout (I10).</summary>
internal static class OidcSessions
{
    /// <summary>The claim the provider's sign-in cookie carries its session id in.</summary>
    public const string SessionIdClaim = "spark_idp_sid";
}
