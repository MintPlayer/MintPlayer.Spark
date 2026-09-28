using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Encodings.Web;

namespace MintPlayer.Spark.Authorization.Identity;

/// <summary>
/// Generates the token, builds the SPA link (<see cref="ISparkAuthLinkBuilder"/>) and hands it to
/// Identity's <see cref="IEmailSender{TUser}"/> — the one seam every account mail goes through, which
/// MailManager implements (#460, M8: SparkMailEmailSender).
/// </summary>
/// <remarks>
/// Never throws on a delivery-side problem: every caller is an endpoint whose answer must not depend
/// on whether a mail went out (that would be an enumeration oracle), so a missing base URL or a
/// failing sender is logged and reported as <see langword="false"/>.
/// </remarks>
internal sealed class SparkAccountMail<TUser>(
    UserManager<TUser> userManager,
    IEmailSender<TUser> emailSender,
    ISparkAuthLinkBuilder links,
    ILogger<SparkAccountMail<TUser>> logger)
    where TUser : SparkUser
{
    /// <summary>Mails the link that confirms the account's email, or (with <paramref name="changedEmail"/>) a change to it.</summary>
    public async Task<bool> SendConfirmationAsync(HttpContext context, TUser user, string email, string? changedEmail = null)
    {
        try
        {
            var token = changedEmail is null
                ? await userManager.GenerateEmailConfirmationTokenAsync(user)
                : await userManager.GenerateChangeEmailTokenAsync(user, changedEmail);
            var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
            var userId = await userManager.GetUserIdAsync(user);
            var link = links.ConfirmEmail(context, userId, code, changedEmail);

            // MailManager's sender takes the raw link (its templates escape) and knows about an email
            // change; any other IEmailSender gets MapIdentityApi's contract: HTML-encoded, markup-safe text.
            if (emailSender is SparkMailEmailSender<TUser> mail)
                await mail.SendConfirmationAsync(user, email, link, changedEmail);
            else
                await emailSender.SendConfirmationLinkAsync(user, email, HtmlEncoder.Default.Encode(link));
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not send the email-confirmation mail for user {UserId}.", user.Id);
            return false;
        }
    }

    /// <summary>Mails the password-reset link.</summary>
    public async Task<bool> SendPasswordResetAsync(HttpContext context, TUser user, string email)
    {
        try
        {
            var token = await userManager.GeneratePasswordResetTokenAsync(user);
            var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
            var link = links.ResetPassword(context, email, code);

            await emailSender.SendPasswordResetLinkAsync(user, email, HtmlEncoder.Default.Encode(link));
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not send the password-reset mail for user {UserId}.", user.Id);
            return false;
        }
    }
}
