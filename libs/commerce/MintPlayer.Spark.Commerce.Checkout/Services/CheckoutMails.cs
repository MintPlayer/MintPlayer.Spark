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
/// information <b>in its body</b> (title and text in the buyer's language) and <b>attaches their PDFs</b> (Q-O9): each
/// accepted version's stored <c>document.pdf</c> is read by MailManager when the mail is sent.
/// </summary>
/// <remarks>
/// <see cref="ICheckoutMailAttachments"/> contributors run first (Invoicing renders the PDFs a version lacks there), so a
/// version with a PDF is a required attachment (the send retries until it can be read) and one still without is
/// optional (left out if it never appears; the body carries the text either way).
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

        // Contributors first: Invoicing renders the PDF of an accepted version that has none (GA2).
        var attachments = await ContributedAttachmentsAsync(new CheckoutMailContext(CheckoutMailKind.Confirmation, order, language), cancellationToken);

        using var session = store.OpenAsyncSession();
        var documentIds = order.Acceptances.Select(a => a.DocumentId).Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var documents = documentIds.Count == 0 ? new Dictionary<string, LegalDocument>()
            : await session.LoadAsync<LegalDocument>(documentIds, cancellationToken);
        attachments.InsertRange(0, LegalDocumentAttachments(documents.Values));

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
                attachments = attachments.Select(a => a.FileName).ToList(),
            },
            Attachments = attachments,
            DeduplicationKey = $"checkout:confirmation:{order.Id}:{order.BackOffice?.ResendCount ?? 0}",
        }, cancellationToken);
    }

    /// <summary>
    /// GA2: one attachment per accepted, published version, read from Commerce's stored <c>document.pdf</c> when the
    /// mail is sent. Required when the version has its PDF; optional when it is still being rendered.
    /// </summary>
    internal static IEnumerable<SparkMailAttachment> LegalDocumentAttachments(IEnumerable<LegalDocument?> documents)
        => documents
            .Where(d => d?.Id is not null && d.PublishedAt is not null)
            .Select(d => SparkMailAttachment.FromDocument(
                d!.Id!, LegalDocument.PdfAttachment, PdfFileName(d.Kind, d.Version), "application/pdf", optional: !d.HasPdf));

    /// <summary>A plain file name: <c>{kind}-v{version}.pdf</c>, the kind reduced to letters, digits and dashes.</summary>
    internal static string PdfFileName(string kind, int version)
    {
        var safe = new string([.. (kind ?? "document").Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '-')]).Trim('-');
        return $"{(safe.Length == 0 ? "document" : safe)}-v{version.ToString(CultureInfo.InvariantCulture)}.pdf";
    }

    /// <summary>The attachments the <see cref="ICheckoutMailAttachments"/> contributors add; a failing one is logged and skipped.</summary>
    internal async Task<List<SparkMailAttachment>> ContributedAttachmentsAsync(CheckoutMailContext context, CancellationToken cancellationToken)
    {
        var result = new List<SparkMailAttachment>();
        foreach (var contributor in services.GetServices<ICheckoutMailAttachments>())
        {
            try
            {
                result.AddRange(await contributor.GetAsync(context, cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "{Contributor} could not attach files to the {Kind} mail of order {OrderId}; sent without them.", contributor.GetType().Name, context.Kind, context.Order.Id);
            }
        }
        return result;
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
    /// the consent primitive's one-click <c>List-Unsubscribe</c> / <c>List-Unsubscribe-Post</c> headers (RFC 8058), which
    /// withdraw the marketing-mail consent itself; without <see cref="ISparkConsentLinks"/>, MailManager's stream header.
    /// </summary>
    public async Task<bool> SendReminderAsync(CheckoutBasket basket, string email, string? name, string? culture, SparkConsentSubject subject, CancellationToken cancellationToken)
    {
        if (Mailer is not { } mailer)
            return false;
        var consentLinks = services.GetService<ISparkConsentLinks>();
        var unsubscribe = consentLinks?.CreateUnsubscribeUrl(subject).ToString();
        var abandoned = options.CurrentValue.AbandonedBaskets;
        await mailer.SendAsync(new SparkMailRequest
        {
            Template = abandoned.MailTemplate,
            To = email,
            ToName = name,
            Culture = culture,
            Stream = abandoned.Stream,
            ListUnsubscribe = true,
            Headers = consentLinks?.CreateListUnsubscribeHeaders(subject),
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
