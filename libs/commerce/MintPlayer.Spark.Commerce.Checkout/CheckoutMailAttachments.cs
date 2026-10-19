using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Checkout.Refunds;
using MintPlayer.Spark.MailManager;

namespace MintPlayer.Spark.Commerce.Checkout;

/// <summary>
/// Adds files to Checkout's durable-medium mails (PRD §4.1 coupling rule: Checkout never references Invoicing, which
/// renders the PDFs). Called before the mail is queued, in registration order; a contributor that throws is logged and
/// skipped, so a rendering failure never holds back the confirmation (its body already carries the documents' text).
/// </summary>
/// <remarks>
/// Invoicing registers one: on <see cref="CheckoutMailKind.Confirmation"/> it renders the accepted legal-document
/// versions that have no PDF yet (GA2), which Checkout then attaches itself; on
/// <see cref="CheckoutMailKind.WithdrawalReceipt"/> it returns the receipt as a PDF (GA1, CRD Art. 11a(3)). Discovered
/// in an application (<c>[SparkDiscoverable]</c>); a library registers its own (scoped, <c>TryAddEnumerable</c>).
/// </remarks>
[SparkDiscoverable]
public interface ICheckoutMailAttachments
{
    /// <summary>The files to attach to the mail described by <paramref name="context"/> (empty when none).</summary>
    Task<IReadOnlyList<SparkMailAttachment>> GetAsync(CheckoutMailContext context, CancellationToken cancellationToken);
}

/// <summary>Which Checkout mail is being queued.</summary>
public enum CheckoutMailKind
{
    /// <summary>The order confirmation (GA2, CRD Art. 8(7)).</summary>
    Confirmation,

    /// <summary>The acknowledgement of a withdrawal (GA1, CRD Art. 11a(3)).</summary>
    WithdrawalReceipt,
}

/// <summary>A Checkout mail about to be queued.</summary>
/// <param name="Kind">The mail.</param>
/// <param name="Order">The order it is about.</param>
/// <param name="Language">The two-letter language the mail renders in.</param>
/// <param name="Withdrawal">For <see cref="CheckoutMailKind.WithdrawalReceipt"/>: the withdrawal acknowledged.</param>
public sealed record CheckoutMailContext(CheckoutMailKind Kind, CheckoutOrder Order, string Language, CheckoutReturn? Withdrawal = null);
