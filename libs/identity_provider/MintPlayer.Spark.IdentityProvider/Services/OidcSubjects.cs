using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using MintPlayer.Spark.IdentityProvider.Models;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// The subject (<c>sub</c>) a client sees for a user (OIDC Core §8, <c>docs/identity_provider_platform_PRD.md</c> D8).
/// <list type="bullet">
/// <item><b>public</b>: the user's id, the same for every client.</item>
/// <item><b>pairwise</b>: a value derived from the client's sector and the user's id, so two clients in
/// different sectors cannot correlate their users. The sector is the host of
/// <see cref="OidcApplication.SectorIdentifierUri"/>, or of the client's single redirect URI.</item>
/// </list>
/// The derivation is keyed with the provider's pairwise salt (<see cref="Salt"/>), set once at startup
/// from <c>OidcKeys</c>, so subjects stay stable across restarts and cannot be recomputed outside.
/// </summary>
public static class OidcSubjects
{
    /// <summary>The provider's pairwise salt. Set by the key ring at startup (I10).</summary>
    internal static byte[] Salt { get; set; } = [];

    public static string For(OidcApplication app, string userId)
    {
        if (!string.Equals(app.SubjectType, "pairwise", StringComparison.OrdinalIgnoreCase))
            return userId;

        var sector = SectorOf(app) ?? app.ClientId;
        using var hmac = new HMACSHA256(Salt.Length > 0 ? Salt : Encoding.UTF8.GetBytes("spark-pairwise"));
        return Base64UrlEncoder.Encode(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{sector.Length}:{sector}|{userId}")));
    }

    /// <summary>
    /// Resolves a subject this provider issued to <paramref name="app"/> back to the user id. A public subject
    /// is the user id; a pairwise one cannot be reversed, so the caller passes the candidate user it expects.
    /// </summary>
    public static bool Matches(OidcApplication app, string subject, string userId) => For(app, userId) == subject;

    private static string? SectorOf(OidcApplication app)
    {
        if (Uri.TryCreate(app.SectorIdentifierUri, UriKind.Absolute, out var sector))
            return sector.Host;
        var hosts = app.RedirectUris
            .Select(u => Uri.TryCreate(u, UriKind.Absolute, out var parsed) ? parsed.Host : null)
            .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return hosts.Count == 1 ? hosts[0] : null;
    }
}
