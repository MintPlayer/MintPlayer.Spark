using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.MailManager;

namespace MintPlayer.Spark.Authorization.Identity;

/// <summary>The account mails' template names (embedded defaults under <c>Mail/SparkAuth</c> in this package).</summary>
public static class SparkAuthMailTemplates
{
    /// <summary>Confirm an address (<c>link</c>, <c>changed_email</c>, <c>valid_for_hours</c>, <c>app_name</c>).</summary>
    public const string ConfirmEmail = "SparkAuth/ConfirmEmail";

    /// <summary>Reset a password (<c>link</c>, <c>valid_for_hours</c>, <c>app_name</c>).</summary>
    public const string PasswordReset = "SparkAuth/PasswordReset";

    /// <summary>Confirm linking an external login (<c>link</c>, <c>provider</c>, <c>provider_identity</c>, <c>valid_for</c>, <c>app_name</c>).</summary>
    public const string LinkConfirmation = "SparkAuth/LinkConfirmation";
}

/// <summary>
/// Identity's <see cref="IEmailSender{TUser}"/> on MailManager (#460, M8). Replaces Identity's no-op
/// <c>DefaultMessageEmailSender</c> — registered by <c>AddIdentityApiEndpoints</c> with TryAdd, which is
/// why it is swapped, not TryAdded. Without an <see cref="ISparkMailer"/> (MailManager not added) it
/// discards like the default, and the D6 startup guard refuses a registration surface in that state.
/// </summary>
/// <remarks>
/// Every mail is queued: <c>Sensitive</c> (the token is encrypted in the message and scrubbed on
/// terminal status, T4) and with <c>ExpiresAtUtc</c> just before the token expires, so a mail delayed
/// by a relay outage or a throttle never arrives dead.
/// </remarks>
public sealed class SparkMailEmailSender<TUser>(
    IServiceProvider services,
    IOptions<DataProtectionTokenProviderOptions> tokenOptions,
    TimeProvider timeProvider,
    ILogger<SparkMailEmailSender<TUser>> logger) : IEmailSender<TUser>
    where TUser : SparkUser
{
    private ISparkMailer? Mailer => services.GetService(typeof(ISparkMailer)) as ISparkMailer;

    /// <summary>Whether MailManager is present; false means this sender discards.</summary>
    public bool CanSend => Mailer is not null;

    /// <inheritdoc />
    public Task SendConfirmationLinkAsync(TUser user, string email, string confirmationLink)
        => SendConfirmationAsync(user, email, WebUtility.HtmlDecode(confirmationLink), changedEmail: null);

    /// <inheritdoc />
    public Task SendPasswordResetLinkAsync(TUser user, string email, string resetLink)
        => SendAsync(user, email, SparkAuthMailTemplates.PasswordReset, new
        {
            link = WebUtility.HtmlDecode(resetLink),
            valid_for_hours = ValidForHours,
        });

    /// <inheritdoc />
    /// <remarks>Spark's own endpoints never send a code (<c>forgotPassword</c> sends a link, #460 M5); a code is sent as a reset mail without a button.</remarks>
    public Task SendPasswordResetCodeAsync(TUser user, string email, string resetCode)
        => SendAsync(user, email, SparkAuthMailTemplates.PasswordReset, new
        {
            link = resetCode,
            valid_for_hours = ValidForHours,
        });

    /// <summary>The confirmation mail with the unencoded link; <paramref name="changedEmail"/> for an email change.</summary>
    public Task SendConfirmationAsync(TUser user, string email, string link, string? changedEmail)
        => SendAsync(user, email, SparkAuthMailTemplates.ConfirmEmail, new
        {
            link,
            changed_email = changedEmail,
            valid_for_hours = ValidForHours,
        });

    private int ValidForHours => Math.Max(1, (int)Math.Floor(tokenOptions.Value.TokenLifespan.TotalHours));

    private async Task SendAsync(TUser user, string email, string template, object data)
    {
        if (Mailer is not { } mailer)
        {
            logger.LogWarning("No MailManager registered: the {Template} mail for user {UserId} was discarded.", template, user.Id);
            return;
        }
        await mailer.SendAsync(new SparkMailRequest
        {
            Template = template,
            To = email,
            ToName = user.UserName,
            // The user's stored preference; null falls through to Spark:Mail:DefaultCulture.
            Culture = user.PreferredCulture,
            Data = data,
            Sensitive = true,
            ExpiresAtUtc = ExpiresAt(tokenOptions.Value.TokenLifespan),
        });
    }

    /// <summary>A little before the token dies: a tenth of its lifespan, at most ten minutes.</summary>
    internal DateTime ExpiresAt(TimeSpan lifespan)
    {
        var margin = TimeSpan.FromTicks(Math.Min(lifespan.Ticks / 10, TimeSpan.FromMinutes(10).Ticks));
        return (timeProvider.GetUtcNow() + lifespan - margin).UtcDateTime;
    }
}

/// <summary>
/// <see cref="ISparkLinkConfirmationSender{TUser}"/> on MailManager: the <c>SparkAuth/LinkConfirmation</c>
/// template with <see cref="SparkLinkConfirmationMessage"/>'s wording. Registered only while an
/// <see cref="ISparkMailer"/> exists, so the ConfirmByEmail startup guard keeps its meaning.
/// </summary>
internal sealed class SparkMailLinkConfirmationSender<TUser>(ISparkMailer mailer, TimeProvider timeProvider) : ISparkLinkConfirmationSender<TUser>
    where TUser : SparkUser, new()
{
    public async Task SendLinkConfirmationAsync(TUser user, string providerDisplayName, string? providerIdentity, string confirmationLink, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(user.Email))
            throw new InvalidOperationException($"User {user.Id} has no email address to send the link confirmation to.");

        var window = SparkExternalLoginLinker<TUser>.ConfirmationWindow;
        await mailer.SendAsync(new SparkMailRequest
        {
            Template = SparkAuthMailTemplates.LinkConfirmation,
            To = user.Email,
            ToName = user.UserName,
            Culture = user.PreferredCulture,
            Data = new
            {
                link = confirmationLink,
                provider = providerDisplayName,
                provider_identity = providerIdentity,
                // Numbers, not an English phrase: the template words and formats them in its culture.
                valid_for_minutes = (int)window.TotalMinutes,
                valid_for_hours = Math.Round(window.TotalHours, 1),
            },
            Sensitive = true,
            ExpiresAtUtc = (timeProvider.GetUtcNow() + window - TimeSpan.FromMinutes(5)).UtcDateTime,
        }, cancellationToken);
    }
}

/// <summary>A recipient's <see cref="SparkUser.PreferredCulture"/>, for mail the app queues by address.</summary>
internal sealed class SparkUserMailCulture<TUser>(UserManager<TUser> users) : ISparkMailRecipientCulture
    where TUser : SparkUser
{
    public async ValueTask<string?> GetCultureAsync(string email, CancellationToken cancellationToken = default)
        => (await users.FindByEmailAsync(email))?.PreferredCulture;
}
