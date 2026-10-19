namespace MintPlayer.Spark.Commerce.Payments;

/// <summary>
/// The evidence pack for one dispute (GB1, §4.5): sections contributed by the packages that know the facts. Commerce
/// gives the audit trail and the accepted terms version (GA2), Checkout the order, its decision log and the buyer's
/// IP/visitor history, Shipping the delivery records, Tickets the scan records. The seller reviews it and submits it
/// (Stripe: by API, <see cref="IPaymentProvider.SubmitDisputeEvidenceAsync"/>; Mollie: as a download for its dashboard).
/// </summary>
public sealed record DisputeEvidencePack
{
    /// <summary>The dispute.</summary>
    public required string DisputeId { get; init; }

    /// <summary>Our order reference.</summary>
    public string? OrderReference { get; init; }

    /// <summary>When the pack was assembled.</summary>
    public required DateTimeOffset AssembledAt { get; init; }

    /// <summary>The sections, in contributor order.</summary>
    public required IReadOnlyList<DisputeEvidenceSection> Sections { get; init; }

    /// <summary>The structured fields a provider's evidence API takes, gathered from the sections.</summary>
    public IReadOnlyDictionary<DisputeEvidenceField, string> Fields { get; init; } = new Dictionary<DisputeEvidenceField, string>();

    /// <summary>The whole pack as plain text: Stripe's <c>uncategorized_text</c> and the download.</summary>
    public string ToText()
    {
        var builder = new System.Text.StringBuilder();
        builder.Append("Dispute ").Append(DisputeId);
        if (OrderReference is not null)
            builder.Append(", order ").Append(OrderReference);
        builder.Append(", assembled ").AppendLine(AssembledAt.ToString("u", System.Globalization.CultureInfo.InvariantCulture));
        foreach (var section in Sections)
        {
            builder.AppendLine().Append("## ").AppendLine(section.Title);
            foreach (var line in section.Lines)
                builder.Append("- ").AppendLine(line);
        }
        return builder.ToString();
    }
}

/// <summary>One section of an evidence pack.</summary>
/// <param name="Source">Who contributed it (<c>commerce.audit</c>, <c>checkout.order</c>, <c>tickets.scans</c>).</param>
/// <param name="Title">A heading.</param>
/// <param name="Lines">The facts, one per line, each with its timestamp where it has one.</param>
/// <param name="Fields">Structured fields this section supplies.</param>
public sealed record DisputeEvidenceSection(
    string Source,
    string Title,
    IReadOnlyList<string> Lines,
    IReadOnlyDictionary<DisputeEvidenceField, string>? Fields = null);

/// <summary>The structured evidence fields providers know (Stripe's <c>evidence[…]</c> names in brackets).</summary>
public enum DisputeEvidenceField
{
    /// <summary><c>customer_name</c>.</summary>
    CustomerName,
    /// <summary><c>customer_email_address</c>.</summary>
    CustomerEmail,
    /// <summary><c>customer_purchase_ip</c>.</summary>
    CustomerPurchaseIp,
    /// <summary><c>billing_address</c>.</summary>
    BillingAddress,
    /// <summary><c>product_description</c>.</summary>
    ProductDescription,
    /// <summary><c>service_date</c> (the event date for a ticket).</summary>
    ServiceDate,
    /// <summary><c>access_activity_log</c> (ticket scans, downloads).</summary>
    AccessActivityLog,
    /// <summary><c>shipping_tracking_number</c>.</summary>
    ShippingTrackingNumber,
    /// <summary><c>shipping_carrier</c>.</summary>
    ShippingCarrier,
    /// <summary><c>shipping_date</c>.</summary>
    ShippingDate,
    /// <summary><c>refund_policy_disclosure</c>.</summary>
    RefundPolicyDisclosure,
    /// <summary><c>cancellation_policy_disclosure</c>.</summary>
    CancellationPolicyDisclosure,
    /// <summary><c>refund_refusal_explanation</c>.</summary>
    RefundRefusalExplanation,
}

/// <summary>
/// Contributes a section to dispute evidence packs (GB1). Discovered in an application (<c>[SparkDiscoverable]</c>);
/// a library registers its own (Checkout, Tickets, Shipping). A contributor that knows nothing about the order answers
/// null.
/// </summary>
[MintPlayer.Spark.Abstractions.SparkDiscoverable]
public interface IDisputeEvidenceContributor
{
    Task<DisputeEvidenceSection?> ContributeAsync(DisputeEvidenceRequest request, CancellationToken cancellationToken);
}

/// <summary>What a contributor is asked about.</summary>
/// <param name="DisputeId">Our dispute document id.</param>
/// <param name="Provider">The provider.</param>
/// <param name="ProviderPaymentId">The disputed payment.</param>
/// <param name="OrderReference">Our order reference (the payment's reference).</param>
/// <param name="SellerOrganizationId">The seller; null for the platform's own sales.</param>
public sealed record DisputeEvidenceRequest(string DisputeId, string Provider, string ProviderPaymentId, string? OrderReference, string? SellerOrganizationId);

/// <summary>The outcome of <see cref="IPaymentProvider.SubmitDisputeEvidenceAsync"/>.</summary>
public enum DisputeEvidenceSubmission
{
    /// <summary>The provider accepted the evidence.</summary>
    Submitted,

    /// <summary>The provider takes evidence only through its dashboard (Mollie); the pack is downloaded instead.</summary>
    ManualOnly,

    /// <summary>The deadline has passed or the dispute is closed.</summary>
    Closed,
}
