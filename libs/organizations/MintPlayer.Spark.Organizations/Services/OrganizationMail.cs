using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.MailManager;
using MintPlayer.Spark.Organizations.Models;

namespace MintPlayer.Spark.Organizations.Services;

/// <summary>The package's mails (<c>Mail/SparkOrganizations/*.mjml</c>, embedded; an application overrides one by file name).</summary>
public static class OrganizationMailTemplates
{
    /// <summary>An address is invited (<c>link</c>, <c>organization</c>, <c>role</c>, <c>inviter</c>, <c>valid_for_days</c>).</summary>
    public const string Invitation = "SparkOrganizations/Invitation";

    /// <summary>An Owner is asked to let a user in (<c>link</c>, <c>organization</c>, <c>role</c>, <c>requester</c>, <c>requester_email</c>, <c>valid_for_days</c>).</summary>
    public const string JoinRequest = "SparkOrganizations/JoinRequest";
}

/// <summary>
/// Builds the acceptance link and queues the invitation and join-request mails through MailManager
/// (<c>docs/seo_analyzer_PRD.md</c> D18).
/// </summary>
/// <remarks>
/// <para>
/// The link is <c>{base}{AcceptPath}/{token}?id={invitationId}</c>, the ng-spark page that posts the
/// two values back on an explicit click (<c>POST /spark/organizations/accept</c>), never a GET that acts:
/// mail scanners open links. The base is the account mails' (<c>Spark:Auth:PublicBaseUrl</c>); the
/// request's own origin is used in Development only, and otherwise nothing is mailed and a warning is
/// logged. A link built from the Host header of an invite request would let its sender point the
/// invitee anywhere.
/// </para>
/// <para>
/// Each mail is <c>Sensitive</c> (the token is encrypted in the queue and scrubbed when delivered) and
/// expires with the invitation. Without MailManager the mail is discarded with a warning, as the
/// account mails are.
/// </para>
/// </remarks>
internal sealed class OrganizationMail(
    IServiceProvider services,
    IOptions<SparkAuthenticationOptions> authOptions,
    IOptions<SparkOrganizationsOptions> options,
    TimeProvider timeProvider,
    ILogger<OrganizationMail> logger,
    IHttpContextAccessor? httpContextAccessor = null,
    IConfiguration? configuration = null,
    IHostEnvironment? environment = null)
{
    /// <summary>Queues <paramref name="template"/> to <paramref name="to"/> with the link for <paramref name="invitation"/>.</summary>
    public async Task SendAsync(string template, string to, string? toName, string? culture, OrganizationInvitation invitation, string token, object data, CancellationToken cancellationToken)
    {
        if (services.GetService<ISparkMailer>() is not { } mailer)
        {
            logger.LogWarning("No MailManager registered: the {Template} mail for {InvitationId} was discarded.", template, invitation.Id);
            return;
        }

        if (Link(invitation.Id!, token) is not { } link)
        {
            logger.LogWarning(
                "The {Template} mail for {InvitationId} was not sent: no public base URL is configured. Set 'Spark:Auth:PublicBaseUrl'. "
                + "Outside Development the request's Host header is not used for mailed links.", template, invitation.Id);
            return;
        }

        var expires = invitation.ExpiresAtUtc ?? timeProvider.GetUtcNow().UtcDateTime.AddDays(14);
        var lifetime = expires - timeProvider.GetUtcNow().UtcDateTime;
        var values = System.Text.Json.JsonSerializer.SerializeToNode(data)!.AsObject();
        values["link"] = link;
        values["valid_for_days"] = Math.Max(1, (int)Math.Ceiling(lifetime.TotalDays));

        await mailer.SendAsync(new SparkMailRequest
        {
            Template = template,
            To = to,
            ToName = toName,
            Culture = culture,
            Data = values,
            // The link is a bearer token: kept out of the delivery log in the clear.
            Sensitive = true,
            // A little before the invitation dies, so a delayed mail never arrives dead.
            ExpiresAtUtc = expires - TimeSpan.FromMinutes(10),
        }, cancellationToken);
    }

    /// <summary>The acceptance link, or null when no trustworthy base URL is known.</summary>
    internal string? Link(string invitationId, string token)
    {
        if (BaseUrl() is not { } baseUrl)
            return null;
        var path = options.Value.AcceptPath.StartsWith('/') ? options.Value.AcceptPath : "/" + options.Value.AcceptPath;
        return QueryHelpers.AddQueryString(baseUrl + path.TrimEnd('/') + "/" + Uri.EscapeDataString(token), "id", invitationId);
    }

    private string? BaseUrl()
    {
        var configured = authOptions.Value.Links.PublicBaseUrl ?? configuration?["Spark:Auth:PublicBaseUrl"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.TrimEnd('/');

        if (environment?.IsDevelopment() == true && httpContextAccessor?.HttpContext is { } context)
            return $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}";

        return null;
    }
}
