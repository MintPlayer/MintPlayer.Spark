using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Invoicing.Pdf;

namespace MintPlayer.Spark.Commerce.Invoicing.Services;

/// <summary>
/// Invoicing's order-event recipient (PRD §4.1 coupling rule: Checkout never references Invoicing).
/// <list type="bullet">
/// <item><c>Placed</c>: the accepted legal-document versions get their PDF rendering (GA2) before the confirmation goes out.</item>
/// <item><c>Confirmed</c>: the invoices or receipts are issued.</item>
/// <item><c>RefundIssued</c> (M9): one credit note per invoice and refund, line by line from <see cref="OrderEvent.Refund"/>.</item>
/// <item><c>LineRefused</c> (M7): a refused line is never invoiced; one already on an invoice is credited in full.</item>
/// <item><c>DisputeLost</c> (GB1): a credit note for what the chargeback took back (cumulative, pro rata).</item>
/// </list>
/// <c>Refunded</c> is <b>not</b> credited: M9 raises <c>RefundIssued</c> for every refund paid (before <c>Refunded</c>),
/// and crediting both would credit twice. Every path is idempotent (documents are keyed by their cause), as the
/// recipient contract requires.
/// </summary>
internal sealed class InvoicingOrderEvents(ISparkInvoicing invoicing, LegalDocumentPdfs legalPdfs) : IOrderEventRecipient
{
    public async Task OnOrderEventAsync(OrderEvent orderEvent, CancellationToken cancellationToken)
    {
        var order = orderEvent.Order;
        switch (orderEvent.Kind)
        {
            case OrderEventKind.Placed:
                await legalPdfs.EnsureForOrderAsync(order, cancellationToken);
                break;
            case OrderEventKind.Confirmed:
                await invoicing.IssueForOrderAsync(order, cancellationToken);
                break;
            case OrderEventKind.RefundIssued when orderEvent.Refund is { } refund:
                // A refund of an order confirmed a moment ago may arrive before the Confirmed delivery finished: issue first.
                await invoicing.IssueForOrderAsync(order, cancellationToken);
                await invoicing.CreditRefundAsync(order, refund, cancellationToken);
                break;
            case OrderEventKind.LineRefused when orderEvent.Reference is { Length: > 0 } lineId:
                // Refused lines are left off the invoice; only a line already invoiced is credited (in full).
                await invoicing.CreditRefusedLineAsync(order, lineId, cancellationToken);
                break;
            case OrderEventKind.DisputeLost:
                await invoicing.IssueForOrderAsync(order, cancellationToken);
                await invoicing.CreditForOrderAsync(order, "dispute:" + (orderEvent.Reference ?? "lost"), cancellationToken);
                break;
        }
    }
}
