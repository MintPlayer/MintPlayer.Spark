using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// Whether the user's consent still stands behind a token.
/// <para>
/// This is an <em>authorization</em> question and it has its own name for that reason. The audit's
/// N1 recorded the inverse mistake — a shared <em>validity</em> resolver was mistaken for a shared
/// authorization check, and "whose token is this?" went unasked by everyone because it looked as
/// though someone had already asked. So this does not live inside <see cref="AccessTokens"/>: it
/// lives here, is called from both places that need it, and is one rule rather than two that will
/// drift.
/// </para>
/// </summary>
internal static class OidcGrants
{
    /// <summary>
    /// True when the token may still be honoured.
    /// <para>
    /// Point-loads by the id the token already carries — never an index query (a withdrawal
    /// seconds earlier must be seen, and this package has shipped that bug three times) and never
    /// a re-derivation from subject and application (synthetic <c>client:</c> subjects do not
    /// derive, and a later change to the subject format would silently orphan every grant).
    /// </para>
    /// </summary>
    public static async Task<bool> PermitsAsync(
        IAsyncDocumentSession session, OidcToken token, CancellationToken ct)
    {
        // No grant to consult. Two populations, and both must be allowed rather than refused:
        // client_credentials tokens, which have no user by construction and are permanent; and
        // tokens minted before the authorization id was threaded through at all, which drain away
        // as they expire. Treating "no grant" as "withdrawn grant" would refuse every machine
        // token in the system on the only path that validates them.
        if (string.IsNullOrEmpty(token.AuthorizationId))
            return true;

        var grant = await session.LoadAsync<OidcAuthorization>(token.AuthorizationId, ct);

        // The grant was deleted. Only reachable if someone removed it deliberately, and removing
        // a grant should end access rather than grant it forever. Same call as a missing token
        // record in AccessTokens.
        if (grant is null)
            return false;

        if (grant.Status != "valid")
            return false;

        // Issued before the last withdrawal, and therefore dead even though the grant is live
        // again.
        //
        // Without this, re-consenting resurrects tokens: the withdrawal sweep is best-effort —
        // it rides an eventually-consistent index and may miss a token minted moments earlier —
        // so a survivor sits at Status "valid" and springs back the instant the user grants the
        // application again. The user's model is "I removed it and then let it back in, so it
        // starts fresh"; the token that survived is exactly the one an attacker would hold.
        //
        // LastRevokedAt is never cleared, which is what makes the best-effort sweep honest: the
        // sweep tidies up, this decides. Both sides are point-loads, so the decision is exact.
        return grant.LastRevokedAt is null || token.CreatedAt > grant.LastRevokedAt;
    }
}
