using MintPlayer.Spark.IdentityProvider.Indexes;
using MintPlayer.Spark.IdentityProvider.Models;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// The tokens a revocation sweeps: the valid tokens issued under one authorization (grant). Read from
/// <see cref="OidcTokens_ByState"/> (#536 D47), so RavenDB builds no auto index, then loaded into the caller's session
/// and checked exactly.
/// </summary>
/// <remarks>
/// A view is eventually consistent on RavenDB: a token issued a moment ago may be missed. Every sweep is bookkeeping,
/// never the enforcement: issuance and introspection point-load the grant or the family and refuse a missed token.
/// </remarks>
internal static class OidcTokenSweep
{
    /// <summary>The valid tokens of <paramref name="authorizationId"/>, tracked by <paramref name="session"/>.</summary>
    public static async Task<IReadOnlyList<OidcToken>> ValidOfAuthorizationAsync(ISparkSession session, string authorizationId, CancellationToken ct)
    {
        var rows = await session.ToListAsync(session.Query<OidcTokenState, OidcTokens_ByState>()
            .Where(t => t.AuthorizationId == authorizationId && t.Status == "valid"), ct);
        if (rows.Count == 0)
            return [];
        var loaded = await session.LoadAsync<OidcToken>(rows.Select(r => r.Id).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase), ct);
        // Ids ignore case; the view may also have matched another casing of the same id.
        return [.. loaded.Values.OfType<OidcToken>()
            .Where(t => t.Status == "valid" && string.Equals(t.AuthorizationId, authorizationId, StringComparison.OrdinalIgnoreCase))];
    }
}
