using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// Takes access back from an application, wholly or per scope (<c>docs/identity_provider_platform_PRD.md</c>
/// D6, Q5): used by the server-rendered <c>/connect/applications</c> page and the SPA's connected-applications page.
/// <para>
/// <b>Per-scope withdrawal</b> narrows the grant and sets <see cref="OidcGrant.LastRevokedAt"/>, so every
/// token issued before it dies, not only those carrying a withdrawn scope: a token's scopes cannot be
/// edited, and the client re-authorizes with what is left. Narrowing a grant to nothing but
/// <c>openid</c> (or nothing) is a withdrawal of the whole grant.
/// </para>
/// </summary>
internal sealed class OidcGrantWithdrawal(IDocumentStore store, OidcAudit audit)
{
    /// <summary>
    /// Withdraws <paramref name="scopes"/> (or the whole grant when null) that <paramref name="userId"/> gave
    /// <paramref name="applicationId"/>. Returns false only when the write kept losing a race.
    /// </summary>
    /// <remarks>
    /// The grant id is derived from the caller's own user id, so no input can reach someone else's
    /// grant, and "not yours" and "no such grant" are the same missing document.
    /// </remarks>
    public async Task<bool> WithdrawAsync(string userId, string applicationId, IReadOnlyCollection<string>? scopes, string? ipAddress, CancellationToken ct)
    {
        var grantId = OidcGrantReference.DocumentId(userId, applicationId);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var session = store.OpenAsyncSession();
            session.Advanced.UseOptimisticConcurrency = true;
            var grant = await session.LoadAsync<OidcGrant>(grantId, ct);
            if (grant is null || grant.Status != "valid")
                return true;

            var now = DateTime.UtcNow;
            var remaining = scopes is null
                ? []
                : grant.GrantedScopes.Where(s => !scopes.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
            var whole = remaining.All(s => string.Equals(s, "openid", StringComparison.OrdinalIgnoreCase));

            if (whole)
            {
                grant.Status = "revoked";
                grant.RevokedAt = now;
            }
            else
            {
                if (remaining.Count == grant.GrantedScopes.Count)
                    return true; // nothing to withdraw
                grant.GrantedScopes = remaining;
            }
            // Never cleared on reinstate: see OidcGrant.LastRevokedAt.
            grant.LastRevokedAt = now;
            await RevokeTokensAsync(session, grantId, ct);
            await audit.RecordAsync(session, whole ? OidcAuditKinds.ConsentWithdrawn : OidcAuditKinds.ConsentNarrowed,
                userId, applicationId, userId, ipAddress,
                new Dictionary<string, string> { ["scopes"] = scopes is null ? "*" : string.Join(' ', scopes) }, ct);

            try
            {
                await session.SaveChangesAsync(ct);
                return true;
            }
            catch (ConcurrencyException)
            {
                // Someone else wrote the grant in between, most likely the user consenting again in
                // another tab. Re-read and decide afresh rather than forcing a stale verdict.
            }
        }
        return false;
    }

    /// <summary>
    /// Revokes every outstanding token issued under the grant, both types: a live refresh token would
    /// let the client mint replacements. The sweep rides an eventually-consistent index, which is
    /// tolerable only because it is not the enforcement: issuance and introspection point-load the
    /// grant and compare <see cref="OidcGrant.LastRevokedAt"/>, so a missed token is still refused.
    /// </summary>
    private static async Task RevokeTokensAsync(IAsyncDocumentSession session, string authorizationId, CancellationToken ct)
    {
        var tokens = await session
            .Query<OidcToken>()
            .Where(t => t.AuthorizationId == authorizationId && t.Status == "valid")
            .ToListAsync(ct);
        foreach (var token in tokens)
        {
            token.Status = "revoked";
            token.RedeemedAt = DateTime.UtcNow;
        }
    }
}
