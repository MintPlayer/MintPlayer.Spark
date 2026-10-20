using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Invoicing.Models;

namespace MintPlayer.Spark.Commerce.Invoicing;

/// <summary>
/// Invoicing (commerce PRD §4.8, D19). Checkout's order events drive it (<see cref="Services.InvoicingOrderEvents"/>):
/// <c>Confirmed</c> issues the invoices or receipts, <c>Refunded</c> and <c>DisputeLost</c> issue credit notes. Every
/// method is idempotent: a document is keyed by what caused it, so a redelivered event issues nothing new.
/// </summary>
public interface ISparkInvoicing
{
    /// <summary>
    /// Issues the documents of a confirmed order: one per issuer (the platform in <c>Platform</c> mode and for D41's
    /// mixed-seller orders, else each seller), a receipt for a consumer unless an invoice was asked for. Returns the
    /// documents, issued now or before.
    /// </summary>
    Task<IReadOnlyList<Invoice>> IssueForOrderAsync(CheckoutOrder order, CancellationToken cancellationToken = default);

    /// <summary>
    /// Brings the credit notes of <paramref name="order"/> up to what was refunded (returned lines in full, the rest
    /// pro rata), as one credit note per invoice keyed by <paramref name="sourceKey"/> (a refund reference or a dispute
    /// id). Nothing is issued when the credited amounts already match.
    /// </summary>
    Task<IReadOnlyList<Invoice>> CreditForOrderAsync(CheckoutOrder order, string sourceKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>LineRefused</c> (M7: an OnPayment race loser, a late payment that could not re-claim; refunded by Checkout with
    /// no <c>RefundIssued</c>): a refused line is never invoiced, so normally nothing happens. When the line is on an
    /// issued document already (the refusal came after the invoice), the line is credited in full, keyed by the line id.
    /// </summary>
    Task<IReadOnlyList<Invoice>> CreditRefusedLineAsync(CheckoutOrder order, string lineId, CancellationToken cancellationToken = default);

    /// <summary>
    /// M9's refund, line by line (<c>OrderEventKind.RefundIssued</c>): one credit note per invoice keyed by the refund id,
    /// crediting each invoice line what the refund paid back for it (price, fees, extras). A line the refund settles in
    /// full is credited to its end, so rounding never leaves a cent on it.
    /// </summary>
    Task<IReadOnlyList<Invoice>> CreditRefundAsync(CheckoutOrder order, Checkout.Refunds.OrderRefundIssued refund, CancellationToken cancellationToken = default);

    /// <summary>
    /// Issues a credit note for explicit amounts per invoice line (gross, positive), for a refund flow that knows its
    /// split (M9's refunds, a diminished-value deduction). Lines mirror the invoice's (strategy H); amounts above what a
    /// line can still credit are refused.
    /// </summary>
    Task<Invoice?> IssueCreditNoteAsync(CreditNoteRequest request, CancellationToken cancellationToken = default);

    /// <summary>The platform's fee invoice to a seller for one month (§4.8), from the fee ledger (GB4). Idempotent per seller and month.</summary>
    Task<Invoice?> IssueFeeInvoiceAsync(string sellerOrganizationId, int year, int month, CancellationToken cancellationToken = default);

    /// <summary>Every document of an order, oldest first.</summary>
    Task<IReadOnlyList<Invoice>> GetForOrderAsync(string orderId, CancellationToken cancellationToken = default);

    /// <summary>Stores the buyer's billing details for an order not invoiced yet. Refused (false) once it is.</summary>
    Task<bool> SaveBillingDetailsAsync(InvoiceBillingDetails details, CancellationToken cancellationToken = default);
}

/// <summary>An explicit credit note.</summary>
/// <param name="InvoiceId">The invoice or receipt credited.</param>
/// <param name="SourceKey">What caused it (idempotency): a refund id, a withdrawal id.</param>
/// <param name="Amounts">Gross amount to credit per invoice line position.</param>
/// <param name="Reason">Printed on the credit note.</param>
public sealed record CreditNoteRequest(string InvoiceId, string SourceKey, IReadOnlyDictionary<int, Money> Amounts, string? Reason = null);

/// <summary>
/// Facts about an order line that Checkout does not store and the place-of-supply rules need: where an event takes
/// place (Art. 53/54), whether it is goods, a different VAT category, the date of supply. Ticketing registers one for
/// ticket types (the venue's country), Shop for products. Discovered (<c>[SparkDiscoverable]</c>); a library registers
/// its own (scoped, <c>TryAddEnumerable</c>). The first non-null answer wins; without one the line is a service in the
/// supplier's country unless the order line says it is physical goods.
/// </summary>
[SparkDiscoverable]
public interface IInvoiceLineFactsSource
{
    Task<InvoiceLineFacts?> GetAsync(CheckoutOrder order, CheckoutOrderLine line, CancellationToken cancellationToken);
}

/// <summary>What <see cref="IInvoiceLineFactsSource"/> answers.</summary>
public sealed record InvoiceLineFacts(string? EventCountry = null, bool? IsGoods = null, string? VatCategory = null, DateOnly? SupplyDate = null);
