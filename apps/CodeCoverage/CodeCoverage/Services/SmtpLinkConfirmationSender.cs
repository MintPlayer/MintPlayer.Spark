using System.Net.Mail;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Identity;

namespace CodeCoverage.Services;

/// <summary>
/// Hands the link-confirmation message to the relay container.
/// </summary>
/// <remarks>
/// <para>
/// The words come from <see cref="SparkLinkConfirmationMessage"/> rather than from here, because
/// they are part of the security design: the reader who did <em>not</em> start the sign-in is the
/// case that matters, and this mail is the only place they can be told that somebody else's
/// sign-in matched their address. D24 keeps them plain strings; a mail manager is separate, future
/// work.
/// </para>
/// <para>
/// ⚠️ <b>The destination is read from the user, never passed in.</b>
/// <see cref="ISparkLinkConfirmationSender{TUser}"/> deliberately takes no address parameter —
/// mailing the address the requesting provider just asserted would confirm nothing, because the
/// person asking chose it.
/// </para>
/// <para>
/// <see cref="SmtpClient"/> rather than MailKit, which is the usual recommendation, because the
/// recommendation is about TLS, authentication and modern server quirks — and none of those are in
/// play. This talks plaintext to a container one hop away on a private network that publishes no
/// ports; the relay owns every hard part of delivery. A dependency would buy nothing.
/// </para>
/// </remarks>
public partial class SmtpLinkConfirmationSender : ISparkLinkConfirmationSender<SparkUser>
{
    [Inject] private readonly IOptions<CoverageMailOptions> options;
    [Inject] private readonly ILogger<SmtpLinkConfirmationSender> logger;

    public async Task SendLinkConfirmationAsync(
        SparkUser user,
        string providerDisplayName,
        string? providerIdentity,
        string confirmationLink,
        CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (!settings.IsConfigured)
        {
            // Registration is conditional on this, so reaching here means the configuration
            // changed under a running process. Throwing beats returning quietly: a confirmation
            // nobody sends is a link nobody makes, and the user sees a sign-in that appears to do
            // nothing at all.
            throw new InvalidOperationException(
                "Coverage:Mail:Host and Coverage:Mail:FromAddress are not configured, so no link "
                + "confirmation can be sent.");
        }

        if (string.IsNullOrWhiteSpace(user.Email))
        {
            // Not reachable through the linking flow, which finds the account *by* its email —
            // but this contract does not promise that, and a silent return would look like a
            // delivery problem rather than a missing address.
            throw new InvalidOperationException(
                $"User '{user.Id}' has no email address, so there is nowhere to send the "
                + "confirmation.");
        }

        using var message = new MailMessage
        {
            From = new MailAddress(settings.FromAddress!, settings.FromName),
            Subject = SparkLinkConfirmationMessage.Subject(providerDisplayName, settings.ApplicationName),
            Body = SparkLinkConfirmationMessage.Body(
                providerDisplayName,
                providerIdentity,
                confirmationLink,
                SparkExternalLoginLinker<SparkUser>.ConfirmationWindow,
                settings.ApplicationName),
            IsBodyHtml = false,
        };
        message.To.Add(new MailAddress(user.Email));

        using var client = new SmtpClient(settings.Host!, settings.Port);
        await client.SendMailAsync(message, cancellationToken);

        // The address is deliberately absent: it is the whole content of the message, and a log
        // line pairing an account with the provider that just asked about it is the disclosure the
        // error codes were careful to avoid.
        logger.LogInformation(
            "Sent an external-login confirmation for {Provider} to the address on account {UserId}.",
            providerDisplayName, user.Id);
    }
}
