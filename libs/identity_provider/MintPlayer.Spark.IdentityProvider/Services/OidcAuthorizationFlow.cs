using MintPlayer.Spark.IdentityProvider.Endpoints;
using MintPlayer.Spark.IdentityProvider.Indexes;
using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// The authorization-request and grant rules that more than one endpoint applies:
/// <c>/connect/authorize</c> and <c>/connect/consent</c> both mint codes and record grants, and the
/// token, revocation, introspection and logout endpoints all resolve a client by its id.
/// </summary>
/// <remarks>
/// Static because every rule here works on the caller's session and needs no service of its own;
/// none of it reads the request or writes a response. Moved out of the old static
/// <c>Authorize</c> handler when it was folded into <see cref="Endpoints.Oidc.OidcAuthorize"/>.
/// </remarks>
internal static class OidcAuthorizationFlow
{
    /// <summary>
    /// Returns the id of the user's valid authorization for this application, widening its
    /// granted scopes to cover <paramref name="scopes"/>, and creating it if there is none.
    /// <para>
    /// Every path that mints a code goes through here, which is what keeps
    /// <see cref="OidcToken.AuthorizationId"/> populated. While it was left empty, both
    /// revocation cascades — the one on the revocation endpoint and the reuse-detection
    /// teardown on the token endpoint — silently swept nothing.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Written through its own session under optimistic concurrency, and retried, rather than on
    /// the caller's session.
    /// <para>
    /// This is a read-modify-write on a document that gates a security decision — the shape this
    /// package has been bitten by three times already (token redemption, grant records, recovery
    /// codes). Two consents racing here silently lost one set of scopes. Worse, once withdrawal
    /// existed: a consent that loaded the grant before a withdrawal committed would write back
    /// <c>Status = "valid"</c> having decided it was not reinstating, and so would skip clearing
    /// the scope history — reopening the escalation withdrawal exists to close, through a race
    /// instead of through the implicit-consent branch.
    /// </para>
    /// <para>
    /// Its own session because the caller's is also carrying the authorization request and the
    /// code, and those must not fail because someone else touched an unrelated grant. Both callers
    /// save their own work afterwards.
    /// </para>
    /// </remarks>
    internal static async Task<string> EnsureAuthorizationAsync(
        IAsyncDocumentSession session,
        OidcApplication app,
        string userId,
        List<string> scopes,
        CancellationToken ct)
    {
        var authorizationId = OidcGrantReference.DocumentId(userId, app.Id!);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await WriteGrantAsync(session.Advanced.DocumentStore, authorizationId, app, userId, scopes, ct);
                return authorizationId;
            }
            catch (ConcurrencyException) when (attempt < 3)
            {
                // Someone else changed the grant between our read and write. Re-read and reapply:
                // the merge is order-independent, so a retry converges rather than clobbering.
            }
        }
    }

    private static async Task WriteGrantAsync(
        IDocumentStore store,
        string authorizationId,
        OidcApplication app,
        string userId,
        List<string> scopes,
        CancellationToken ct)
    {
        using var session = store.OpenAsyncSession();
        session.Advanced.UseOptimisticConcurrency = true;

        var auth = await session.LoadAsync<OidcGrant>(authorizationId, ct);

        if (auth == null)
        {
            auth = new OidcGrant
            {
                Id = authorizationId,
                ApplicationId = app.Id!,
                Subject = userId,
                CreatedAt = DateTime.UtcNow,
            };

            await session.StoreAsync(auth, ct);
        }

        // Consenting again reinstates a grant the user previously withdrew — that is precisely
        // what they have just asked for. Tokens issued before the withdrawal stay revoked; only
        // the grant itself comes back.
        //
        // But it comes back as *this* consent, not as everything the grant ever accumulated.
        // The merge below only ever adds, and the list is never reset, so unioning on
        // reinstatement handed back the full historical set: withdraw a grant carrying
        // `api.admin`, let the client ask for `openid` alone, and the user silently got
        // `api.admin` again — thereafter auto-approved, because a grant that covers the request
        // skips the consent screen entirely. Withdrawal has to mean the scope history is gone
        // too, or it is not withdrawal.
        var reinstating = auth.Status != "valid";
        if (reinstating)
            auth.GrantedScopes.Clear();

        auth.Status = "valid";
        auth.RevokedAt = null;
        // LastRevokedAt is deliberately NOT cleared: tokens issued before the withdrawal must stay
        // dead even though the grant is live again.

        foreach (var s in scopes)
        {
            if (!auth.GrantedScopes.Contains(s, StringComparer.OrdinalIgnoreCase))
                auth.GrantedScopes.Add(s);
        }

        await session.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Mints an authorization code for an already-validated request, saves it, and returns the URL
    /// that delivers it back to the client. Everything the code carries comes from
    /// <paramref name="request"/>, never from the current HTTP request.
    /// </summary>
    internal static async Task<string> IssueCodeAsync(
        IAsyncDocumentSession session,
        OidcToken request,
        CancellationToken ct)
    {
        var code = OidcTokenReference.GenerateValue();

        var token = new OidcToken
        {
            // The id is the hash of the code, so redemption is a strongly-consistent
            // point-load and the code itself is never persisted.
            Id = OidcTokenReference.DocumentId(code),
            ApplicationId = request.ApplicationId,
            AuthorizationId = request.AuthorizationId,
            Subject = request.Subject,
            Type = OidcTokenTypes.AuthorizationCode,
            CodeChallenge = request.CodeChallenge,
            CodeChallengeMethod = request.CodeChallengeMethod,
            RedirectUri = request.RedirectUri,
            Scopes = [.. request.Scopes],
            Status = "valid",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5), // 5 minute lifetime
            Nonce = request.Nonce,
            AuthTime = request.AuthTime,
        };

        // A request mints exactly one code. Re-submitting the consent form, or replaying the
        // handle from browser history, finds a consumed request rather than a second code.
        request.Status = "consumed";

        await session.StoreExpiringAsync(token, ct);
        await session.SaveChangesAsync(ct);

        return RedirectUrl.With(request.RedirectUri!,
            ("code", code),
            ("state", request.State));
    }

    /// <summary>
    /// Loads the request behind a <c>request_id</c>, or null if it is unknown, expired,
    /// already used, or belongs to a different signed-in user.
    /// </summary>
    internal static async Task<OidcToken?> LoadPendingRequestAsync(
        IAsyncDocumentSession session, string requestId, string userId, CancellationToken ct)
    {
        var request = await session.LoadAsync<OidcToken>(
            OidcRequestReference.DocumentId(requestId), ct);

        if (request is not { Type: OidcTokenTypes.AuthorizationRequest, Status: "pending" })
            return null;

        if (request.ExpiresAt < DateTime.UtcNow)
            return null;

        // The handle is bound to the user it was issued for: one user must not be able to
        // hand another a link that consents on their behalf.
        if (!string.Equals(request.Subject, userId, StringComparison.Ordinal))
            return null;

        return request;
    }

    internal static async Task<OidcApplication?> FindApplicationByClientIdAsync(
        IAsyncDocumentSession session, string clientId, CancellationToken ct)
    {
        // exact: true because RavenDB compares strings case-insensitively by default, which
        // would make "acmeapp" resolve the application registered as "AcmeApp" — impersonation
        // by casing, on the lookup that decides which client every other check is applied to.
        return await session.Query<OidcApplication, OidcApplications_ByClientId>()
            .Where(a => a.ClientId == clientId, exact: true)
            .FirstOrDefaultAsync(ct);
    }
}
