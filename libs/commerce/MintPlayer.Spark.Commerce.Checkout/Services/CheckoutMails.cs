using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Consent;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Models;
using MintPlayer.Spark.Commerce.Payments;
using MintPlayer.Spark.MailManager;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Checkout.Services;

/// <summary>
/// Checkout's mails through MailManager (optional: without it nothing is sent and a warning is logged once per mail).
/// The confirmation is the durable medium of CRD Art. 8(7) (GA2): it carries the accepted terms and the withdrawal
/// information <b>in its body</b> (title and text in the buyer's language) plus links to their PDFs.
/// </summary>
/// <remarks>
/// ⚠️ MailManager's <c>SparkMailRequest</c> has no attachments, so the documents travel in the body instead of as PDF
/// attachments (Q-O9). A mail body is a durable medium in itself; attaching the PDFs needs an attachment field in
/// MailManager (reported to the lead).
/// </remarks>
internal sealed class CheckoutMails(
    IServiceProvider services,
    IDocumentStore store,
    IOptionsMonitor<SparkCommerceCheckoutOptions> options,
    ILogger<CheckoutMails> logger)
{
    private ISparkMailer? Mailer => services.GetService<ISparkMailer>();

    /// <summary>The app's public origin for links in mail.</summary>
    public string? PublicBaseUrl => SparkPublicBaseUrl.Resolve(
        services.GetService<IOptionsMonitor<SparkCommercePaymentsOptions>>()?.CurrentValue.PublicBaseUrl,
        services.GetService<IConfiguration>(), services.GetService<IHostEnvironment>(), services.GetService<IHttpContextAccessor>()?.HttpContext);

    public string Absolute(string path) => PublicBaseUrl is { } origin ? origin.TrimEnd('/') + path : path;

    public string OrderPath(CheckoutOrder order) => options.CurrentValue.OrderPagePath.Replace("{number}", Uri.EscapeDataString(order.Number), StringComparison.Ordinal);

    /// <summary>GA2: the confirmation on a durable medium. Deduplicated per order and resend count.</summary>
    public async Task SendConfirmationAsync(CheckoutOrder order, CancellationToken cancellationToken)
    {
        if (Mailer is not { } mailer)
        {
            logger.LogWarning("No MailManager: the confirmation of order {OrderId} is not mailed (GA2 needs a durable medium).", order.Id);
            return;
        }
        var (email, name, culture) = await RecipientAsync(order, cancellationToken);
        if (email is null)
            return;
        var language = Language(culture);

        using var session = store.OpenAsyncSession();
        var documentIds = order.Acceptances.Select(a => a.DocumentId).ToList();
        var documents = documentIds.Count == 0 ? new Dictionary<string, LegalDocument>()
            : await session.LoadAsync<LegalDocument>(documentIds, cancellationToken);

        await mailer.SendAsync(new SparkMailRequest
        {
            Template = options.CurrentValue.ConfirmationMailTemplate,
            To = email,
            ToName = name,
            Culture = culture,
            Data = new
            {
                number = order.Number,
                buyer_name = name,
                total = Format(order.Total),
                lines = order.Lines.Where(l => l.Status == OrderLineStatus.Active).Select(l => new
                {
                    title = l.Title?.GetValue(language) ?? l.ItemId,
                    quantity = l.Quantity,
                    total = Format(l.Total),
                    units = string.Join(", ", l.Units),
                    id_check = l.IdCheckRequired,
                    minimum_age = l.MinimumAge,
                }).ToList(),
                extras = order.Extras.Select(e => new { label_key = e.LabelKey, amount = Format(e.Amount) }).ToList(),
                documents = documents.Values.Where(d => d is not null).Select(d => new
                {
                    kind = d!.Kind,
                    version = d.Version,
                    title = d.Title?.GetValue(language) ?? d.Kind,
                    body = d.Body?.GetValue(language),
                    pdf = d.HasPdf ? Absolute("/spark/commerce/legal/pdf?id=" + Uri.EscapeDataString(d.Id!)) : null,
                }).ToList(),
                link = Absolute(OrderPath(order)),
                // M9, GA1: the order page with the withdrawal function; for a guest, the signed order link (D4).
                withdrawal_link = services.GetService<Refunds.RefundMails>()?.OrderLink(order),
            },
            DeduplicationKey = $"checkout:confirmation:{order.Id}:{order.BackOffice?.ResendCount ?? 0}",
        }, cancellationToken);
    }

    /// <summary>GB7: the payment link of a phone order.</summary>
    public async Task SendPaymentLinkAsync(CheckoutOrder order, string checkoutUrl, CancellationToken cancellationToken)
    {
        if (Mailer is not { } mailer)
        {
            logger.LogWarning("No MailManager: the payment link of order {OrderId} is not mailed.", order.Id);
            return;
        }
        var (email, name, culture) = await RecipientAsync(order, cancellationToken);
        if (email is null)
            return;
        await mailer.SendAsync(new SparkMailRequest
        {
            Template = options.CurrentValue.PaymentLinkMailTemplate,
            To = email,
            ToName = name,
            Culture = culture,
            Data = new { number = order.Number, total = Format(order.Total), pay_url = checkoutUrl, buyer_name = name },
            DeduplicationKey = $"checkout:payment-link:{order.Id}:{order.Payment?.Attempt ?? 0}",
        }, cancellationToken);
    }

    /// <summary>
    /// GC1: the abandoned-basket reminder, marketing mail: it carries the consent unsubscribe link (X21) in the body and
    /// MailManager's one-click <c>List-Unsubscribe</c> for its stream.
    /// </summary>
    public async Task<bool> SendReminderAsync(CheckoutBasket basket, string email, string? name, string? culture, SparkConsentSubject subject, CancellationToken cancellationToken)
    {
        if (Mailer is not { } mailer)
            return false;
        var unsubscribe = services.GetService<ISparkConsentLinks>()?.CreateUnsubscribeUrl(subject).ToString();
        var abandoned = options.CurrentValue.AbandonedBaskets;
        await mailer.SendAsync(new SparkMailRequest
        {
            Template = abandoned.MailTemplate,
            To = email,
            ToName = name,
            Culture = culture,
            Stream = abandoned.Stream,
            ListUnsubscribe = true,
            Data = new
            {
                lines = basket.Lines.Count,
                basket_url = Absolute("/checkout/basket"),
                unsubscribe_url = unsubscribe,
                buyer_name = name,
            },
            DeduplicationKey = $"checkout:reminder:{basket.Id}:{basket.UpdatedAt.ToUnixTimeSeconds()}",
        }, cancellationToken);
        return true;
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

    private static string Language(string? culture)
        => culture is { Length: >= 2 } c ? c[..2].ToLowerInvariant() : TranslatedString.DefaultLanguage;

    internal static string Format(Money money)
        => money.Amount.ToString("0.00##", CultureInfo.InvariantCulture) + " " + money.Currency;
}
