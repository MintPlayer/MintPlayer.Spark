using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.IdentityProvider.Indexes;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;
using static MintPlayer.Spark.IdentityProvider.Endpoints.ConnectPage;

namespace MintPlayer.Spark.IdentityProvider.Endpoints;

/// <summary>
/// Where a user sees what they have granted, and takes it back.
/// <para>
/// Consent was recorded from the first commit and consulted nowhere, and there was no way to
/// withdraw it: RFC 7009's <c>/connect/revoke</c> is client-facing — it demands client
/// credentials and refuses a token not issued to the authenticating client — so a user could
/// never call it. This page is the missing half. Withdrawal here is what
/// <c>Token.GrantPermitsIssuanceAsync</c> and <c>AccessTokens.ResolveAsync</c> read.
/// </para>
/// <para>
/// Withdrawal is <b>all-or-nothing per application</b>. RFC 6749 §6 requires a rotated refresh
/// token's scope to be identical to the presented one's, which makes "narrow the grant and keep
/// refreshing" self-contradictory; and every major provider takes the same all-or-nothing line
/// for the user-facing action. The grant is marked revoked rather than deleted, so the audit
/// trail survives and the issuance checks have a state to read.
/// </para>
/// </summary>
internal static class ConnectedApplications
{
    public static async Task HandleGet(HttpContext context)
    {
        var ct = context.RequestAborted;

        var userId = await context.GetInteractiveUserIdAsync();
        if (string.IsNullOrEmpty(userId))
        {
            var returnUrl = context.Request.Path + context.Request.QueryString;
            context.Response.Redirect($"/connect/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
            return;
        }

        var store = context.RequestServices.GetRequiredService<IDocumentStore>();
        using var session = store.OpenAsyncSession();

        var grants = await LoadGrantsAsync(session, userId, ct);

        // Names for the rows. A grant whose application has been deleted still lists and still
        // withdraws — that is the grant a user is most likely to want gone, and dropping the row
        // would leave it un-withdrawable through the only surface that can withdraw it.
        var apps = await session.LoadAsync<OidcApplication>(
            grants.Select(g => g.ApplicationId).Distinct(), ct);

        await WritePageAsync(context, grants, apps, Notice(context.Request.Query["status"].FirstOrDefault()));
    }

    public static async Task HandleRevoke(HttpContext context)
    {
        var ct = context.RequestAborted;

        var userId = await context.GetInteractiveUserIdAsync();
        if (string.IsNullOrEmpty(userId))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("Not authenticated.");
            return;
        }

        var form = await context.Request.ReadFormAsync(ct);
        var applicationId = form["application_id"].FirstOrDefault();

        var store = context.RequestServices.GetRequiredService<IDocumentStore>();

        // The form names an *application*; the grant id is derived from the session's user. So
        // there is no parameter a forged post could set to reach someone else's grant — the
        // property is structural rather than a check that has to be remembered. It also means
        // "not yours" and "no such grant" are the same missing document here, so the response
        // cannot distinguish them and cannot be used to probe which grants exist.
        var withdrawn = string.IsNullOrEmpty(applicationId)
            || await TryWithdrawAsync(store, OidcAuthorizationReference.DocumentId(userId, applicationId), ct);

        // Reporting "Access removed" when the write lost a race would be the worst possible
        // outcome here: the user believes they have taken access back and has no reason to look
        // again. Saying so only when the write actually landed.
        context.Response.Redirect(withdrawn
            ? "/connect/applications?status=revoked"
            : "/connect/applications?status=failed");
    }

    /// <summary>
    /// Marks the grant revoked and tears down its tokens, or reports that it could not.
    /// <para>
    /// Under optimistic concurrency, and retried: this is a read-modify-write on a document that
    /// gates a security decision, racing directly against a consent that would set it back to
    /// valid. Last-write-wins here means a user is told access was removed while it is live.
    /// </para>
    /// <para>
    /// A grant that is already revoked counts as success — the user asked for it gone and it is
    /// gone. Withdrawing is idempotent, not a transaction that has to be the one that did it.
    /// </para>
    /// </summary>
    private static async Task<bool> TryWithdrawAsync(IDocumentStore store, string grantId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var session = store.OpenAsyncSession();
            session.Advanced.UseOptimisticConcurrency = true;

            var grant = await session.LoadAsync<OidcAuthorization>(grantId, ct);
            if (grant is null || grant.Status != "valid")
                return true;

            var now = DateTime.UtcNow;
            grant.Status = "revoked";
            grant.RevokedAt = now;
            // Never cleared on reinstate — see OidcAuthorization.LastRevokedAt.
            grant.LastRevokedAt = now;

            await RevokeTokensAsync(session, grantId, ct);

            try
            {
                await session.SaveChangesAsync(ct);
                return true;
            }
            catch (ConcurrencyException)
            {
                // Someone else wrote the grant in between — most likely the user consenting again
                // in another tab. Re-read and decide afresh rather than forcing a stale verdict.
            }
        }

        return false;
    }

    /// <summary>
    /// Revokes every outstanding token issued under this grant — both types.
    /// <para>
    /// The existing cascade on the revocation endpoint sweeps only <c>access_token</c>, which is
    /// right for its own purpose and wrong here: leaving a refresh token alive would let the
    /// client mint replacements, and withdrawal would achieve nothing.
    /// </para>
    /// <para>
    /// The sweep rides an eventually-consistent index, so a token issued in the moments before
    /// the withdrawal may be missed. That is tolerable only because it is not the enforcement:
    /// issuance point-loads this grant and refuses, and introspection does the same, so a missed
    /// token is caught the next time anything asks about it. The sweep is cleanup.
    /// </para>
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

    private static async Task<List<OidcAuthorization>> LoadGrantsAsync(
        IAsyncDocumentSession session, string userId, CancellationToken ct)
    {
        // Display only — see the index's own remarks. Status is filtered in memory rather than
        // as a query predicate so a stale index cannot decide what the user is shown.
        var all = await session
            .Query<OidcAuthorization, OidcAuthorizations_BySubject>()
            .Where(a => a.Subject == userId, exact: true)
            .ToListAsync(ct);

        return [.. all.Where(a => a.Status == "valid").OrderBy(a => a.ApplicationId, StringComparer.Ordinal)];
    }

    private static string? Notice(string? status) => status switch
    {
        "revoked" => "Access removed.",
        "failed" => "That did not go through — the application was being re-authorized at the same time. Please try again.",
        _ => null,
    };

    private static async Task WritePageAsync(
        HttpContext context,
        List<OidcAuthorization> grants,
        Dictionary<string, OidcApplication> apps,
        string? notice)
    {
        context.Response.ContentType = "text/html; charset=utf-8";

        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head>");
        sb.Append("<title>Connected applications</title>");
        sb.Append("<style>");
        sb.Append("body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;max-width:560px;margin:60px auto;padding:0 20px}");
        sb.Append("h2{color:#333}");
        sb.Append(".app{padding:16px 0;border-bottom:1px solid #eee;display:flex;align-items:flex-start;gap:16px}");
        sb.Append(".app-body{flex:1}.app-name{font-weight:600}");
        sb.Append(".scopes{color:#666;font-size:13px;margin-top:4px}");
        sb.Append(".btn{padding:8px 16px;border:none;border-radius:6px;font-size:14px;cursor:pointer;background:#dc3545;color:white}");
        sb.Append(".notice{background:#d1e7dd;color:#0f5132;padding:10px 14px;border-radius:6px;margin-bottom:20px}");
        sb.Append(".empty{color:#666}.footnote{color:#666;font-size:13px;margin-top:24px}");
        sb.Append("</style></head><body>");
        sb.Append("<h2>Connected applications</h2>");

        if (notice != null)
            sb.Append("<div class=\"notice\">").Append(Encode(notice)).Append("</div>");

        if (grants.Count == 0)
        {
            sb.Append("<p class=\"empty\">No applications have access to your account.</p>");
        }
        else
        {
            foreach (var grant in grants)
            {
                // Fall back to the raw id when the application is gone, rather than hiding the row.
                var name = apps.TryGetValue(grant.ApplicationId, out var app) && app is not null
                    ? app.DisplayName
                    : grant.ApplicationId;

                sb.Append("<div class=\"app\"><div class=\"app-body\">");
                sb.Append("<div class=\"app-name\">").Append(Encode(name)).Append("</div>");

                if (grant.GrantedScopes.Count > 0)
                {
                    sb.Append("<div class=\"scopes\">")
                      .Append(Encode(string.Join(", ", grant.GrantedScopes)))
                      .Append("</div>");
                }

                sb.Append("</div>");
                sb.Append("<form method=\"post\" action=\"/connect/applications/revoke\">");
                AppendAntiforgery(sb, context);
                AppendHidden(sb, "application_id", grant.ApplicationId);
                sb.Append("<button type=\"submit\" class=\"btn\">Remove access</button>");
                sb.Append("</form></div>");
            }

            // Said plainly rather than implied away. An access token is a signed JWT that a
            // resource server may check without ever asking us again, so we cannot recall one
            // already in flight — only stop new ones being issued. A page that implied otherwise
            // would be worse than no page.
            sb.Append("<p class=\"footnote\">Removing access stops an application from getting new ")
              .Append("access to your account. Access it already holds may keep working for up to an hour.</p>");
        }

        sb.Append("</body></html>");

        await context.Response.WriteAsync(sb.ToString());
    }
}
