using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.IdentityProvider.Configuration;
using MintPlayer.Spark.IdentityProvider.Models;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>The team mails: the invitation link (D3).</summary>
internal sealed class OidcTeamMail(
    OidcPortalMail mail,
    OidcPortalLinks links,
    IHttpContextAccessor httpContextAccessor,
    ISparkStore store,
    OidcUserDocuments users,
    SparkIdentityProviderOptions options,
    ILogger<OidcTeamMail> logger)
{
    public async Task SendInvitationAsync(OidcApplication app, string email, string role, string token, CancellationToken ct)
    {
        if (httpContextAccessor.HttpContext is not { } http)
            return;

        string link;
        try
        {
            link = links.Invitation(http, app.Id.Value, token);
        }
        catch (InvalidOperationException ex)
        {
            // Never mail a link built from the request's Host header (OidcPortalLinks).
            logger.LogWarning(ex, "The invitation to {ApplicationId} was not mailed.", app.Id);
            return;
        }

        string? inviter = null;
        if (app.Members.LastOrDefault(m => m.Email == email)?.InvitedBy is { } inviterId)
        {
            using var session = store.OpenSession();
            inviter = (await users.LoadAsync(session, [inviterId], ct)).Values.FirstOrDefault()?.UserName;
        }

        await mail.SendAsync(email, null, null, OidcPortalMailTemplates.Invitation, new
        {
            link,
            application = app.DisplayName,
            role,
            inviter = inviter ?? app.DisplayName,
            valid_for_days = (int)Math.Ceiling(options.Apps.InvitationLifetime.TotalDays),
        }, ct);
    }
}
