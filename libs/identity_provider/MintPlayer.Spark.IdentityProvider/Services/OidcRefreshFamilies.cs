using System.Diagnostics.CodeAnalysis;
using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// Refresh-token families (<see cref="OidcRefreshFamily"/>): started when a code or device code is redeemed for a refresh
/// token, advanced at every rotation, revoked on a theft signal, and consulted by every use of a token in them. Every read
/// is a point-load by the id the token carries, never an index query (<c>docs/spark_mcp_PRD.md</c> decision log,
/// "Refresh-token families exact").
/// </summary>
internal static class OidcRefreshFamilies
{
    private const string CollectionPrefix = "OidcRefreshFamilies/";

    /// <summary>
    /// The family started by redeeming the code or device code recorded at <paramref name="originTokenId"/>. Derived from
    /// it, so a code presented again finds the family it started without anything stored on the code.
    /// </summary>
    public static string DocumentId(string originTokenId) => OpaqueHandle.DocumentId(CollectionPrefix, originTokenId);

    /// <summary>
    /// Starts the family of <paramref name="refreshToken"/>, issued by redeeming <paramref name="origin"/>, and enrols it
    /// and <paramref name="accessToken"/>, issued beside it. Stored in <paramref name="session"/>, so the family commits
    /// with the tokens or not at all.
    /// </summary>
    public static async Task StartAsync(
        IAsyncDocumentSession session, OidcToken origin, OidcToken refreshToken, OidcToken accessToken, CancellationToken ct)
    {
        var family = new OidcRefreshFamily
        {
            Id = DocumentId(origin.Id!),
            ApplicationId = refreshToken.ApplicationId,
            AuthorizationId = refreshToken.AuthorizationId,
            Subject = refreshToken.Subject,
            Generation = 1,
            CreatedAt = refreshToken.CreatedAt,
            ExpiresAt = Later(refreshToken.ExpiresAt, accessToken.ExpiresAt),
        };
        await session.StoreAsync(family, ct);
        OidcExpiry.Stamp(session, family, family.ExpiresAt);
        Enrol(family, refreshToken, accessToken);
    }

    /// <summary>
    /// A rotation: <paramref name="successor"/> becomes the family's active refresh token. The family is written with the
    /// rotation, so under the session's optimistic concurrency a rotation racing another rotation of the family, or its
    /// revocation, fails as a whole.
    /// </summary>
    public static void Advance(IAsyncDocumentSession session, OidcRefreshFamily family, OidcToken successor, OidcToken accessToken)
    {
        family.Generation++;
        family.RotatedAt = successor.CreatedAt;
        family.ExpiresAt = Later(family.ExpiresAt, Later(successor.ExpiresAt, accessToken.ExpiresAt));
        OidcExpiry.Stamp(session, family, family.ExpiresAt);
        Enrol(family, successor, accessToken);
    }

    private static void Enrol(OidcRefreshFamily family, OidcToken refreshToken, OidcToken accessToken)
    {
        refreshToken.FamilyId = family.Id;
        refreshToken.Generation = family.Generation;
        accessToken.FamilyId = family.Id;
    }

    /// <summary>The family <paramref name="token"/> belongs to; null when it has none, or the family is gone.</summary>
    public static async Task<OidcRefreshFamily?> LoadAsync(IAsyncDocumentSession session, OidcToken token, CancellationToken ct)
        => string.IsNullOrEmpty(token.FamilyId) ? null : await session.LoadAsync<OidcRefreshFamily>(token.FamilyId, ct);

    /// <summary>
    /// Whether presenting <paramref name="refreshToken"/> is a reuse: it was rotated away, or it is not its family's
    /// current generation. A token revoked otherwise (logout, a withdrawal, the disable cascade, RFC 7009) is no theft
    /// signal; it is simply not <see cref="IsActive"/>.
    /// </summary>
    public static bool IsReuse(OidcToken refreshToken, OidcRefreshFamily? family)
        => refreshToken.Status == "redeemed"
           || (refreshToken.Status == "valid" && family is not null && family.Generation != refreshToken.Generation);

    /// <summary>
    /// Whether <paramref name="refreshToken"/> is its family's active refresh token: unspent, at the current generation of
    /// a family that stands. A refresh token without a family is not: every refresh token is issued into one.
    /// </summary>
    public static bool IsActive(OidcToken refreshToken, [NotNullWhen(true)] OidcRefreshFamily? family)
        => refreshToken.Status == "valid" && family is { Status: "valid" } && family.Generation == refreshToken.Generation;

    /// <summary>
    /// Whether the family <paramref name="token"/> was issued in still stands. A token issued in no family has none to
    /// consult (client credentials, a code redeemed without a refresh token); a family that is gone fails closed, as a
    /// missing grant does in <see cref="OidcGrants.PermitsAsync"/>.
    /// </summary>
    public static async Task<bool> PermitsAsync(IAsyncDocumentSession session, OidcToken token, CancellationToken ct)
        => string.IsNullOrEmpty(token.FamilyId)
           || await session.LoadAsync<OidcRefreshFamily>(token.FamilyId, ct) is { Status: "valid" };

    /// <summary>
    /// Revokes the family <paramref name="familyId"/> in <paramref name="session"/>, which must not check change vectors:
    /// the write then wins against a rotation that committed after the family was read here, and a rotation that read the
    /// family before this commits fails on its own optimistic concurrency. Revoked is final, so overwriting a rotation's
    /// generation loses nothing. Returns false when there is nothing to revoke: no family, gone, or already revoked.
    /// </summary>
    public static async Task<bool> RevokeAsync(IAsyncDocumentSession session, string? familyId, string reason, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(familyId) || await session.LoadAsync<OidcRefreshFamily>(familyId, ct) is not { Status: "valid" } family)
            return false;

        family.Status = "revoked";
        family.RevokedAt = DateTime.UtcNow;
        family.RevokedReason = reason;
        return true;
    }

    private static DateTime Later(DateTime a, DateTime b) => a > b ? a : b;
}
