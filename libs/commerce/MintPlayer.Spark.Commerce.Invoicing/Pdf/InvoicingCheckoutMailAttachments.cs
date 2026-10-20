using System.Globalization;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Checkout.Refunds;
using MintPlayer.Spark.MailManager;

namespace MintPlayer.Spark.Commerce.Invoicing.Pdf;

/// <summary>
/// Invoicing's files for Checkout's durable-medium mails (PRD §4.1: Checkout defines the seam, Invoicing renders):
/// <list type="bullet">
/// <item><see cref="CheckoutMailKind.Confirmation"/>: renders the PDF of every accepted legal-document version that has
/// none yet (GA2), so Checkout attaches it as a required file instead of racing the <c>Placed</c> recipient. Returns
/// nothing itself: Checkout attaches the stored PDFs.</item>
/// <item><see cref="CheckoutMailKind.WithdrawalReceipt"/>: the receipt as a PDF (GA1, CRD Art. 11a(4) as inserted by
/// Directive (EU) 2023/2673: the content of the withdrawal and the date and time of its receipt).</item>
/// </list>
/// </summary>
internal sealed class InvoicingCheckoutMailAttachments(LegalDocumentPdfs legalPdfs) : ICheckoutMailAttachments
{
    public async Task<IReadOnlyList<SparkMailAttachment>> GetAsync(CheckoutMailContext context, CancellationToken cancellationToken)
    {
        switch (context.Kind)
        {
            case CheckoutMailKind.Confirmation:
                await legalPdfs.EnsureForOrderAsync(context.Order, cancellationToken);
                return [];
            case CheckoutMailKind.WithdrawalReceipt when context.Withdrawal is { } withdrawal:
                return [SparkMailAttachment.FromBytes(ReceiptFileName(context.Order), "application/pdf", RenderWithdrawalReceipt(context.Order, withdrawal, context.Language))];
            default:
                return [];
        }
    }

    internal static string ReceiptFileName(CheckoutOrder order)
    {
        var number = new string([.. order.Number.Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '-')]);
        return $"withdrawal-receipt-{number}.pdf";
    }

    /// <summary>Title, order number, date and time of receipt (UTC offset stated), source, lines and the note.</summary>
    internal static byte[] RenderWithdrawalReceipt(CheckoutOrder order, CheckoutReturn withdrawal, string language)
    {
        var l = Labels.GetValueOrDefault(language) ?? Labels["en"];
        var pdf = new MinimalPdf();
        float y = 60;
        void Line(string text, float size = 10, bool bold = false)
        {
            foreach (var part in MinimalPdf.Wrap(text, 495, size, bold))
            {
                if (y > 790)
                {
                    pdf.NewPage();
                    y = 60;
                }
                pdf.Text(50, y, part, size, bold);
                y += size + 4;
            }
        }

        Line(l.Title, 16, bold: true);
        y += 6;
        Line($"{l.Order}: {order.Number}");
        Line($"{l.ReceivedAt}: {withdrawal.ReceivedAt.ToString("yyyy-MM-dd HH:mm:ss 'UTC'zzz", CultureInfo.InvariantCulture)}");
        Line($"{l.Source}: {withdrawal.Source}");
        if (withdrawal.Name is { Length: > 0 } name)
            Line($"{l.Buyer}: {name}");
        y += 8;
        Line(l.Lines, 11, bold: true);
        foreach (var line in withdrawal.Lines)
        {
            var title = order.Lines.FirstOrDefault(x => x.LineId == line.LineId)?.Title?.GetValue(language) ?? line.LineId;
            var status = line.Eligible ? "" : $" ({l.NotEligible}{(line.IneligibleReason is { Length: > 0 } r ? ": " + r : "")})";
            Line($"{line.Quantity.ToString(CultureInfo.InvariantCulture)} × {title}{status}");
        }
        if (withdrawal.RefundDueBy is { } due)
        {
            y += 8;
            Line($"{l.RefundDueBy}: {due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        }
        if (withdrawal.Note is { Length: > 0 } note)
        {
            y += 8;
            Line(note);
        }
        return pdf.Build();
    }

    private sealed record ReceiptLabels(string Title, string Order, string ReceivedAt, string Source, string Buyer, string Lines, string NotEligible, string RefundDueBy);

    private static readonly Dictionary<string, ReceiptLabels> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = new("Acknowledgement of withdrawal", "Order", "Received on", "Received through", "Buyer", "Withdrawn items", "not eligible", "Refund due by"),
        ["nl"] = new("Ontvangstbevestiging herroeping", "Bestelling", "Ontvangen op", "Ontvangen via", "Koper", "Herroepen artikelen", "niet in aanmerking", "Terugbetaling uiterlijk"),
        ["fr"] = new("Accusé de réception de la rétractation", "Commande", "Reçue le", "Reçue par", "Acheteur", "Articles concernés", "non éligible", "Remboursement au plus tard le"),
    };
}
