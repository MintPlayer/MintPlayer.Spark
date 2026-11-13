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
/// <para>
/// Never throws on a delivery-side problem: every caller is an endpoint whose answer must not depend
/// on whether a mail went out (that would be an enumeration oracle), so a missing base URL or a
/// failing sender is logged and reported as <see langword="false"/>.
/// </para>
/// <para>
/// The <c>Queue…</c> methods serve the anonymous endpoints that take an address and answer the same
/// whether it belongs to anyone (A11). They do the same work for an unknown address as for a known
/// one — a token for <see cref="Unknown"/> and its link, then discarded — and hand the delivery to
/// <see cref="SparkAccountMailDispatcher{TUser}"/> instead of awaiting it, so neither the answer nor
/// its timing tells the two apart.
/// </para>
/// </remarks>
internal sealed class SparkAccountMail<TUser>(
    UserManager<TUser> userManager,
    IEmailSender<TUser> emailSender,
    ISparkAuthLinkBuilder links,
    SparkAccountMailDispatcher<TUser> dispatcher,
    ILogger<SparkAccountMail<TUser>> logger)
    where TUser : SparkUser, new()
{
    /// <summary>
    /// A user that is no account: tokens are generated and verified for it on the unknown-address path,
    /// so that path costs what the known one does. Its id is random, so no token made for it can match
    /// a stored user, and it is never stored.
    /// </summary>
    internal static TUser Unknown() => new()
    {
        Id = $"SparkUsers/unknown-{Guid.NewGuid():N}",
        SecurityStamp = Guid.NewGuid().ToString("N"),
    };

    /// <summary>Mails the link that confirms the account's email, or (with <paramref name="changedEmail"/>) a change to it.</summary>
    public async Task<bool> SendConfirmationAsync(HttpContext context, TUser user, string email, string? changedEmail = null)
    {
        try
        {
            var link = await ConfirmationLinkAsync(context, user, changedEmail);
            await DeliverConfirmationAsync(emailSender, user, email, link, changedEmail);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not send the email-confirmation mail for user {UserId}.", user.Id);
            return false;
        }
    }

    /// <summary>
    /// <c>resendConfirmationEmail</c> and <c>register</c>: queues the confirmation link for <paramref name="user"/>, or, when
    /// it is <see langword="null"/> (no such account, or nothing to confirm), does the same token and
    /// link work for <see cref="Unknown"/> and sends nothing.
    /// </summary>
    public async Task QueueConfirmationAsync(HttpContext context, TUser? user, string email)
    {
        try
        {
            var link = await ConfirmationLinkAsync(context, user ?? Unknown(), changedEmail: null);
            if (user is not null)
                dispatcher.Enqueue("email-confirmation", user.Id, sender => DeliverConfirmationAsync(sender, user, email, link, changedEmail: null));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not queue the email-confirmation mail for user {UserId}.", user?.Id);
        }
    }

    /// <summary>
    /// <c>forgotPassword</c>: queues the password-reset link for <paramref name="user"/>, or, when it
    /// is <see langword="null"/>, does the same token and link work for <see cref="Unknown"/> and sends
    /// nothing.
    /// </summary>
    public async Task QueuePasswordResetAsync(HttpContext context, TUser? user, string email)
    {
        try
        {
            var token = await userManager.GeneratePasswordResetTokenAsync(user ?? Unknown());
            var link = HtmlEncoder.Default.Encode(links.ResetPassword(context, email, Encode(token)));
            if (user is not null)
                dispatcher.Enqueue("password-reset", user.Id, sender => sender.SendPasswordResetLinkAsync(user, email, link));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not queue the password-reset mail for user {UserId}.", user?.Id);
        }
    }

    /// <summary>
    /// <c>register</c> with an address that is already <paramref name="user"/>'s (A13): queues a notice
    /// to the owner with a password-reset link. One token and one queued mail, as the fresh registration's
    /// <see cref="QueueConfirmationAsync"/> does, so the two cost the same.
    /// </summary>
    /// <remarks>
    /// Identity's <see cref="IEmailSender{TUser}"/> has no such mail: Spark's MailManager sender sends
    /// <see cref="SparkAuthMailTemplates.AccountExists"/>, and an app's own sender gets the password-reset
    /// link, the one thing the owner may need from it.
    /// </remarks>
    public async Task QueueAccountExistsAsync(HttpContext context, TUser user, string email)
    {
        try
        {
            var token = await userManager.GeneratePasswordResetTokenAsync(user);
            var link = links.ResetPassword(context, email, Encode(token));
            dispatcher.Enqueue("account-exists", user.Id, sender => sender is SparkMailEmailSender<TUser> mail
                ? mail.SendAccountExistsAsync(user, email, link)
                : sender.SendPasswordResetLinkAsync(user, email, HtmlEncoder.Default.Encode(link)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not queue the account-exists mail for user {UserId}.", user.Id);
        }
    }

    private async Task<string> ConfirmationLinkAsync(HttpContext context, TUser user, string? changedEmail)
    {
        var token = changedEmail is null
            ? await userManager.GenerateEmailConfirmationTokenAsync(user)
            : await userManager.GenerateChangeEmailTokenAsync(user, changedEmail);
        var userId = await userManager.GetUserIdAsync(user);
        return links.ConfirmEmail(context, userId, Encode(token), changedEmail);
    }

    // MailManager's sender takes the raw link (its templates escape) and knows about an email change;
    // any other IEmailSender gets MapIdentityApi's contract: HTML-encoded, markup-safe text.
    private static Task DeliverConfirmationAsync(IEmailSender<TUser> sender, TUser user, string email, string link, string? changedEmail)
        => sender is SparkMailEmailSender<TUser> mail
            ? mail.SendConfirmationAsync(user, email, link, changedEmail)
            : sender.SendConfirmationLinkAsync(user, email, HtmlEncoder.Default.Encode(link));

    private static string Encode(string token) => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
}
