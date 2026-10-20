using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Checkout.Services;
using MintPlayer.Spark.MailManager;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Checkout.Refunds;

/// <summary>
/// The refund flows' mails through MailManager (optional; without it a warning is logged). The withdrawal receipt is the
/// acknowledgement on a durable medium of CRD Art. 11a(4) (as inserted by Directive (EU) 2023/2673): it states the
/// content of the withdrawal and the date and time it was received. The mail body is the receipt (a durable medium in
/// itself); an <see cref="ICheckoutMailAttachments"/> contributor adds it as a PDF too (Invoicing does, Q-O9).
/// </summary>
internal sealed class RefundMails(
    IServiceProvider services,
    IDocumentStore store,
    CheckoutMails checkoutMails,
    OrderLinks links,
    IOptionsMonitor<SparkCommerceCheckoutOptions> options,
    ILogger<RefundMails> logger)
{
    private ISparkMailer? Mailer => services.GetService<ISparkMailer>();

    private CheckoutRefundOptions Options => options.CurrentValue.Refunds;

    /// <summary>The order page link for the buyer: the signed guest link for a guest order, else the plain page.</summary>
    public string OrderLink(CheckoutOrder order)
    {
        if (order.BuyerUserId is null && links.Create(order.Id!) is { } token)
            return checkoutMails.Absolute(Options.GuestOrderPagePath
                .Replace("{number}", Uri.EscapeDataString(order.Number), StringComparison.Ordinal)
                .Replace("{token}", Uri.EscapeDataString(token), StringComparison.Ordinal));
        return checkoutMails.Absolute(checkoutMails.OrderPath(order));
    }

    /// <summary>GA1: the acknowledgement of a withdrawal with its date and time (deduplicated per return).</summary>
    public async Task SendWithdrawalReceiptAsync(CheckoutOrder order, CheckoutReturn withdrawal, CancellationToken cancellationToken)
    {
        if (Mailer is not { } mailer)
        {
            logger.LogWarning("No MailManager: the withdrawal receipt of order {OrderId} is not mailed (CRD Art. 11a needs a durable medium).", order.Id);
            return;
        }
        var (email, name, culture) = await RecipientAsync(order, cancellationToken);
        email = withdrawal.Email ?? email;
        name = withdrawal.Name ?? name;
        if (email is null)
            return;
        var language = culture is { Length: >= 2 } c ? c[..2].ToLowerInvariant() : TranslatedString.DefaultLanguage;
        // GA1: the receipt as a PDF from a contributor (Invoicing renders it); the body stays the receipt as well.
        var attachments = await checkoutMails.ContributedAttachmentsAsync(new CheckoutMailContext(CheckoutMailKind.WithdrawalReceipt, order, language, withdrawal), cancellationToken);
        await mailer.SendAsync(new SparkMailRequest
        {
            Template = Options.WithdrawalReceiptMailTemplate,
            Attachments = attachments,
            To = email,
            ToName = name,
            Culture = culture,
            Data = new
            {
                number = order.Number,
                buyer_name = name,
                // ISO 8601 with offset: the receipt states the date and time of receipt unambiguously.
                received_at = withdrawal.ReceivedAt.ToString("yyyy-MM-dd HH:mm:ss 'UTC'zzz", System.Globalization.CultureInfo.InvariantCulture),
                source = withdrawal.Source.ToString(),
                note = withdrawal.Note,
                lines = withdrawal.Lines.Select(l => new
                {
                    title = order.Lines.FirstOrDefault(x => x.LineId == l.LineId)?.Title?.GetValue(language) ?? l.LineId,
                    quantity = l.Quantity,
                    eligible = l.Eligible,
                    reason = l.IneligibleReason,
                }).ToList(),
                withheld = withdrawal.Status == ReturnStatus.AwaitingGoods,
                refund_due_by = withdrawal.RefundDueBy?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                seller_pays_return = withdrawal.SellerPaysReturnShipping,
                link = OrderLink(order),
            },
            DeduplicationKey = $"checkout:withdrawal:{order.Id}:{withdrawal.ReturnId}",
        }, cancellationToken);
    }

    /// <summary>D20: the keep-or-refund offer after a reschedule (deduplicated per window and order).</summary>
    public async Task<bool> SendRescheduleNoticeAsync(CheckoutOrder order, CheckoutRescheduleWindow window, CancellationToken cancellationToken)
    {
        if (Mailer is not { } mailer)
            return false;
        var (email, name, culture) = await RecipientAsync(order, cancellationToken);
        if (email is null)
            return false;
        await mailer.SendAsync(new SparkMailRequest
        {
            Template = Options.RescheduleNoticeMailTemplate,
            To = email,
            ToName = name,
            Culture = culture,
            Data = new
            {
                number = order.Number,
                buyer_name = name,
                closes_at = window.ClosesAt.ToString("yyyy-MM-dd HH:mm 'UTC'zzz", System.Globalization.CultureInfo.InvariantCulture),
                new_date = window.NewServiceDate?.ToString("yyyy-MM-dd HH:mm 'UTC'zzz", System.Globalization.CultureInfo.InvariantCulture),
                note = window.Note,
                link = OrderLink(order),
            },
            DeduplicationKey = $"checkout:reschedule:{window.Id}:{order.Id}",
        }, cancellationToken);
        return true;
    }

    /// <summary>The refund is paid out.</summary>
    public async Task SendRefundIssuedAsync(CheckoutOrder order, CheckoutRefund refund, CancellationToken cancellationToken)
    {
        if (Mailer is not { } mailer || refund.Amount.IsZero)
            return;
        var (email, name, culture) = await RecipientAsync(order, cancellationToken);
        if (email is null)
            return;
        await mailer.SendAsync(new SparkMailRequest
        {
            Template = Options.RefundIssuedMailTemplate,
            To = email,
            ToName = name,
            Culture = culture,
            Data = new { number = order.Number, buyer_name = name, amount = CheckoutMails.Format(refund.Amount), kept = refund.Deducted.IsZero ? null : CheckoutMails.Format(refund.Deducted), link = OrderLink(order) },
            DeduplicationKey = $"checkout:refund:{order.Id}:{refund.RefundId}",
        }, cancellationToken);
    }

    private async Task<(string? Email, string? Name, string? Culture)> RecipientAsync(CheckoutOrder order, CancellationToken cancellationToken)
    {
        string? culture = null;
        var email = order.BuyerEmail;
        var name = order.BuyerName;
        if (order.BuyerUserId is { } userId)
        {
            using var session = store.OpenAsyncSession();
            if (await session.LoadAsync<SparkUser>(userId, cancellationToken) is { } user)
            {
                culture = user.PreferredCulture;
                email ??= user.Email;
                name ??= user.UserName;
            }
        }
        return (email, name, culture);
    }
}
