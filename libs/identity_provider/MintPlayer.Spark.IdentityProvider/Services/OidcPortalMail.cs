using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.MailManager;
using Raven.Client.Documents;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>The developer portal's mails (<c>Mail/SparkIdentityProvider/*.mjml</c>, embedded; an application overrides one by file name).</summary>
public static class OidcPortalMailTemplates
{
    public const string DeveloperApproved = "SparkIdentityProvider/DeveloperApproved";
    public const string DeveloperRejected = "SparkIdentityProvider/DeveloperRejected";
    public const string Invitation = "SparkIdentityProvider/Invitation";
    public const string ScopeApprovalDecided = "SparkIdentityProvider/ScopeApprovalDecided";
    public const string GoLiveDecided = "SparkIdentityProvider/GoLiveDecided";
}

/// <summary>
/// Sends the portal's mails through MailManager when the application registered it, and logs that a
/// mail was discarded otherwise, the way the account mails do (<c>SparkMailEmailSender</c>).
/// </summary>
internal sealed class OidcPortalMail(IServiceProvider services, IDocumentStore store, ILogger<OidcPortalMail> logger)
{
    private sealed class Recipient
    {
        public string? Email { get; set; }
        public string? UserName { get; set; }
        public string? PreferredCulture { get; set; }
    }

    /// <summary>Mails a user by id: their address and language are read from their document.</summary>
    public async Task SendToUserAsync(string userId, string template, object data, CancellationToken ct = default)
    {
        using var session = store.OpenAsyncSession();
        var user = await session.LoadAsync<Recipient>(userId, ct);
        if (string.IsNullOrEmpty(user?.Email))
        {
            logger.LogWarning("The {Template} mail for user {UserId} was not sent: the user has no email address.", template, userId);
            return;
        }
        await SendAsync(user.Email, user.UserName, user.PreferredCulture, template, data, ct);
    }

    /// <summary>Mails an address directly (an invitation to someone's typed address).</summary>
    public async Task SendAsync(string to, string? toName, string? culture, string template, object data, CancellationToken ct = default)
    {
        if (services.GetService<ISparkMailer>() is not { } mailer)
        {
            logger.LogWarning("No MailManager registered: the {Template} mail was discarded.", template);
            return;
        }

        await mailer.SendAsync(new SparkMailRequest
        {
            Template = template,
            To = to,
            ToName = toName,
            Culture = culture,
            Data = data,
            // Invitation links are bearer tokens: kept out of the delivery log in the clear.
            Sensitive = true,
        }, ct);
    }
}
