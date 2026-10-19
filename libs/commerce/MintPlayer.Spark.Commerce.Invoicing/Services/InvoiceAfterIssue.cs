using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Invoicing.EInvoicing;
using MintPlayer.Spark.Commerce.Invoicing.Models;
using MintPlayer.Spark.Commerce.Invoicing.Pdf;
using MintPlayer.Spark.Commerce.Payments;
using MintPlayer.Spark.MailManager;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Invoicing.Services;

/// <summary>
/// What follows an issued document: the money audit entry, the Peppol transmission for business documents, and the
/// mail with the PDF attached (Q-O9) and a signed link to download it again. Each step is idempotent (the transmission
/// by its id, the mail by a deduplication key), so running it again for a document issued earlier sends nothing twice.
/// </summary>
internal sealed class InvoiceAfterIssue(
    IServiceProvider services,
    IDocumentStore store,
    EInvoiceDispatcher dispatcher,
    InvoiceLinks links,
    IInvoicePdfRenderer pdfRenderer,
    ILogger<InvoiceAfterIssue> logger)
{
    public const string AuditIssued = "commerce-invoicing.issued";

    public async Task RunAsync(Invoice invoice, string? recipientEmail, CancellationToken cancellationToken)
    {
        if (services.GetService<ICommerceAudit>() is { } audit)
        {
            using var session = store.OpenAsyncSession();
            await audit.RecordAsync(session, new CommerceAuditEntry
            {
                Action = AuditIssued,
                SubjectId = invoice.Id!,
                OrganizationId = invoice.SellerOrganizationId,
                After = invoice.TotalGross,
                At = invoice.IssuedAt,
                Data = new Dictionary<string, string> { ["number"] = invoice.Number, ["kind"] = invoice.Kind.ToString(), ["order"] = invoice.OrderId ?? "" },
            }, cancellationToken);
            try { await session.SaveChangesAsync(cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "The audit entry of {Invoice} was not stored.", invoice.Number); }
        }

        try { await dispatcher.QueueAsync(invoice, cancellationToken); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "The Peppol transmission of {Invoice} could not be queued.", invoice.Number); }

        if (recipientEmail is not { Length: > 0 } || services.GetService<ISparkMailer>() is not { } mailer)
            return;
        await mailer.SendAsync(new SparkMailRequest
        {
            Template = "SparkInvoicing/InvoiceIssued",
            // The invoice itself, rendered now: a sent invoice must not change, and the issued document is immutable.
            Attachments = PdfAttachment(invoice),
            To = recipientEmail,
            ToName = invoice.Buyer.Name is { Length: > 0 } n ? n : null,
            Culture = invoice.Language,
            Sensitive = true,
            DeduplicationKey = "invoicing:issued:" + invoice.Id,
            Data = new
            {
                number = invoice.Number,
                kind = invoice.Kind.ToString(),
                credit = invoice.IsCredit,
                total = invoice.TotalGross.Amount.ToString("0.00", CultureInfo.InvariantCulture),
                currency = invoice.Currency,
                order = invoice.OrderNumber,
                issuer = invoice.Issuer.Name,
                link = links.PdfLink(invoice.Id!),
            },
        }, cancellationToken);
    }

    /// <summary>The PDF as an attachment; on a rendering failure the mail goes without it (the link still works).</summary>
    internal IReadOnlyList<SparkMailAttachment>? PdfAttachment(Invoice invoice)
    {
        try
        {
            return [SparkMailAttachment.FromBytes(PdfFileName(invoice.Number), "application/pdf", pdfRenderer.Render(invoice))];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The PDF of {Invoice} could not be rendered for its mail; it is sent with the link only.", invoice.Number);
            return null;
        }
    }

    internal static string PdfFileName(string number)
        => new string([.. number.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-')]) + ".pdf";
}

/// <summary>
/// Signed links to a document's PDF for guests (who have no account to sign in with): the invoice id protected by
/// ASP.NET Data Protection, valid for <see cref="Lifetime"/>. A signed-in buyer, seller or platform member reads the PDF
/// through the row rule instead.
/// </summary>
internal sealed class InvoiceLinks(IServiceProvider services, IDataProtectionProvider protection, TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(90);

    private ITimeLimitedDataProtector Protector => protection.CreateProtector("MintPlayer.Spark.Commerce.Invoicing.PdfLink").ToTimeLimitedDataProtector();

    public string Token(string invoiceId) => Protector.Protect(invoiceId, time.GetUtcNow() + Lifetime);

    /// <summary>The invoice id a token names, or null when it is forged or expired.</summary>
    public string? Read(string? token)
    {
        if (token is not { Length: > 0 and < 2048 })
            return null;
        try
        {
            var id = Protector.Unprotect(token, out var expiration);
            return expiration > time.GetUtcNow() ? id : null;
        }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }

    public string PdfLink(string invoiceId)
    {
        var path = "/spark/commerce/invoicing/pdf?token=" + Uri.EscapeDataString(Token(invoiceId));
        var origin = SparkPublicBaseUrl.Resolve(
            services.GetService<IOptionsMonitor<SparkCommercePaymentsOptions>>()?.CurrentValue.PublicBaseUrl,
            services.GetService<IConfiguration>(), services.GetService<IHostEnvironment>(), services.GetService<IHttpContextAccessor>()?.HttpContext);
        return origin is { } o ? o.TrimEnd('/') + path : path;
    }
}
