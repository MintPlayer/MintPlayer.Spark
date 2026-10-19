using System.Security.Cryptography;
using System.Text;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Commerce.Invoicing.Models;

// Names carry the "Invoice"/"Invoicing" prefix: the same CLR simple name in two libraries silently shares a collection (S0).

/// <summary>What an invoice document is.</summary>
public enum InvoiceKind
{
    /// <summary>A B2C receipt: a simplified invoice in the sense of Directive 2006/112/EC Art. 220a/226b (buyer details optional).</summary>
    Receipt,
    /// <summary>A full invoice (Art. 226): B2B, or a consumer who asked for one.</summary>
    Invoice,
    /// <summary>A credit note correcting an invoice or receipt (Art. 219); its lines mirror the credited document's (strategy H, S21).</summary>
    CreditNote,
    /// <summary>The platform's invoice to a seller for its fees (§4.8).</summary>
    FeeInvoice,
    /// <summary>A credit note on a fee invoice (fees refunded later than the invoice).</summary>
    FeeCreditNote,
}

/// <summary>
/// An issued invoice, receipt or credit note (§4.8). <b>Never altered</b> (§4.15): it is written once, in the
/// cluster-wide unit of work that takes its number (so a number exists exactly when its document does: gap-free), and
/// only ever deleted whole when its retention floor ends. Anything that changes later (the e-invoice transmission, a
/// mail) lives in its own document. <see cref="ContentHash"/> is the SHA-256 of the document as issued, so a later
/// change is detectable.
/// </summary>
public sealed class Invoice
{
    public string? Id { get; set; }

    public InvoiceKind Kind { get; set; }

    /// <summary>The full number as printed (<c>F2026-000042</c>).</summary>
    public string Number { get; set; } = "";

    /// <summary>The series prefix.</summary>
    public string Series { get; set; } = "";

    /// <summary>The numbering year (the UTC year of issue).</summary>
    public int Year { get; set; }

    /// <summary>The position in the series and year: 1, 2, 3, … without gaps.</summary>
    public long Sequence { get; set; }

    /// <summary>The issuer key the counter belongs to: <c>platform</c> or the seller's organization id.</summary>
    public string IssuerKey { get; set; } = "";

    /// <summary>The seller (row security, exports); null for the platform's own documents.</summary>
    public string? SellerOrganizationId { get; set; }

    public DateTimeOffset IssuedAt { get; set; }

    /// <summary>The invoice date (BT-2), the UTC calendar date of issue (see the guide on dates).</summary>
    public DateOnly IssueDate { get; set; }

    /// <summary>The date of supply when it differs (BT-72): the event date of admission lines, else the order's confirmation date.</summary>
    public DateOnly? SupplyDate { get; set; }

    public string Currency { get; set; } = "EUR";

    /// <summary>The language the document is written in (titles resolved at issue).</summary>
    public string Language { get; set; } = "en";

    /// <summary>The order it invoices; null for a fee invoice.</summary>
    public string? OrderId { get; set; }

    public string? OrderNumber { get; set; }

    /// <summary>The buyer's account (row security for the buyer); null for a guest.</summary>
    public string? BuyerUserId { get; set; }

    /// <summary>The event that caused it (idempotency key): <c>confirmed</c>, a refund reference, a dispute id, a fee period.</summary>
    public string SourceKey { get; set; } = "";

    /// <summary>For a credit note: the document it credits (BT-25).</summary>
    public string? CreditedInvoiceId { get; set; }

    public string? CreditedInvoiceNumber { get; set; }

    public DateOnly? CreditedInvoiceDate { get; set; }

    public InvoiceParty Issuer { get; set; } = new();

    public InvoiceParty Buyer { get; set; } = new();

    /// <summary>Business-to-business (a buyer with a VAT id or company id).</summary>
    public bool Business { get; set; }

    public InvoicePricingBasis PricingBasis { get; set; }

    public List<InvoiceLine> Lines { get; set; } = [];

    public List<InvoiceVatGroup> VatBreakdown { get; set; } = [];

    /// <summary>Sum of line net amounts (BT-106).</summary>
    public Money TotalNet { get; set; } = Money.Zero("EUR");

    /// <summary>Total VAT (BT-110).</summary>
    public Money TotalVat { get; set; } = Money.Zero("EUR");

    /// <summary>Total with VAT (BT-112); what the buyer paid (or gets back, for a credit note: positive amounts).</summary>
    public Money TotalGross { get; set; } = Money.Zero("EUR");

    /// <summary>Mentions the law requires (Art. 226(11), (11a)): "Reverse charge", the exemption references, the IOSS number.</summary>
    public List<string> Notes { get; set; } = [];

    /// <summary>GC8: the IOSS number the consignment was declared under.</summary>
    public string? IossNumber { get; set; }

    /// <summary>The payment terms text (BT-20) and means; null for a paid order.</summary>
    public string? PaymentTerms { get; set; }

    /// <summary>Already paid (BT-113): the whole total for a confirmed order.</summary>
    public Money? Paid { get; set; }

    /// <summary>SHA-256 (hex) of the document as issued (with this field empty).</summary>
    public string ContentHash { get; set; } = "";

    /// <summary>The idempotency id of a document caused by <paramref name="sourceKey"/> for <paramref name="subject"/>.</summary>
    public static string IdFor(InvoiceKind kind, string subject, string sourceKey)
        => $"Invoices/{kind.ToString().ToLowerInvariant()}-{Hash(subject + "\n" + sourceKey)}";

    internal static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32];

    public bool IsCredit => Kind is InvoiceKind.CreditNote or InvoiceKind.FeeCreditNote;
}

/// <summary>A party on an invoice, as issued (a snapshot: later changes to the seller or buyer never reach it).</summary>
public sealed class InvoiceParty
{
    public string Name { get; set; } = "";

    /// <summary>The VAT id with its country prefix.</summary>
    public string? VatId { get; set; }

    /// <summary>The VAT id was valid in VIES when the invoice was issued (the consultation is kept in <see cref="VatCheckReference"/>).</summary>
    public bool VatIdVerified { get; set; }

    /// <summary>The VIES consultation number (proof of the check).</summary>
    public string? VatCheckReference { get; set; }

    public string? CompanyId { get; set; }

    /// <summary>ISO 3166-1 alpha-2.</summary>
    public string? Country { get; set; }

    /// <summary>A sub-national code when a VAT territory applies (<c>ES-CN</c>).</summary>
    public string? Territory { get; set; }

    public List<string> AddressLines { get; set; } = [];

    public string? PostalCode { get; set; }

    public string? City { get; set; }

    public string? Email { get; set; }

    /// <summary>Peppol participant id <c>{scheme}:{id}</c>.</summary>
    public string? PeppolId { get; set; }

    public string? Iban { get; set; }
}

/// <summary>What an invoice line came from.</summary>
public enum InvoiceLineSource
{
    Item,
    Fee,
    Adjustment,
    Extra,
    PlatformFee,
}

/// <summary>One invoice line (EN 16931 BG-25), as issued.</summary>
public sealed class InvoiceLine
{
    /// <summary>Position on the document, from 1.</summary>
    public int Position { get; set; }

    /// <summary>The order line (or extra code, fee entry) it came from.</summary>
    public string SourceLineId { get; set; } = "";

    public InvoiceLineSource Source { get; set; }

    /// <summary>For a credit-note line: the position of the credited line.</summary>
    public int? CreditedPosition { get; set; }

    /// <summary>The title in <see cref="Invoice.Language"/>.</summary>
    public string Title { get; set; } = "";

    public int Quantity { get; set; } = 1;

    /// <summary>Net amount of the line (BT-131).</summary>
    public Money Net { get; set; } = Money.Zero("EUR");

    public Money Vat { get; set; } = Money.Zero("EUR");

    public Money Gross { get; set; } = Money.Zero("EUR");

    /// <summary>The host category the rate came from.</summary>
    public string? VatCategory { get; set; }

    /// <summary>The UNCL 5305 code (BT-151): S, Z, E, AE, K, G, O.</summary>
    public string VatCode { get; set; } = "S";

    public int VatRateBasisPoints { get; set; }

    /// <summary>The member state whose VAT applies (GA4); null outside the scope.</summary>
    public string? TaxCountry { get; set; }

    /// <summary>The <see cref="Vat.IPlaceOfSupplyRule"/> that decided (<c>distance-sale</c>, <c>admission</c>, …).</summary>
    public string? PlaceOfSupplyRule { get; set; }

    public string? ExemptionReason { get; set; }

    public string? ExemptionReasonCode { get; set; }

    /// <summary>The channel the line was sold in (DAC7 ticket flag, reports).</summary>
    public string? Channel { get; set; }

    /// <summary>Goods (DAC7 sale of goods; OSS goods vs services).</summary>
    public bool IsGoods { get; set; }
}

/// <summary>One VAT breakdown row (BG-23): per category code, rate and taxing country.</summary>
public sealed class InvoiceVatGroup
{
    public string VatCode { get; set; } = "S";

    public int RateBasisPoints { get; set; }

    public string? TaxCountry { get; set; }

    /// <summary>BT-116.</summary>
    public Money Taxable { get; set; } = Money.Zero("EUR");

    /// <summary>BT-117.</summary>
    public Money Vat { get; set; } = Money.Zero("EUR");

    public string? ExemptionReason { get; set; }

    public string? ExemptionReasonCode { get; set; }
}

/// <summary>The gap-free counter of one issuer, series and year (a compare-exchange value, X12).</summary>
public sealed record InvoiceCounter(string Owner, long Last);

/// <summary>
/// The buyer's billing details for one order (a company name, VAT id, address, Peppol id), given at checkout or on the
/// order page before the invoice is issued. Checkout's order carries no billing address; this document is
/// Invoicing's own (coupling rule, §4.1).
/// </summary>
public sealed class InvoiceBillingDetails
{
    public string? Id { get; set; }

    public string OrderId { get; set; } = "";

    /// <summary>The buyer asked for a full invoice (a consumer may, Art. 220(1)).</summary>
    public bool InvoiceRequested { get; set; }

    public string? Name { get; set; }

    public string? VatId { get; set; }

    public string? CompanyId { get; set; }

    public string? Country { get; set; }

    public List<string> AddressLines { get; set; } = [];

    public string? PostalCode { get; set; }

    public string? City { get; set; }

    public string? PeppolId { get; set; }

    /// <summary>GB6/GC8: goods leave the EU (proof of export kept by the customs flow); 0 % export (Art. 146).</summary>
    public string? ShipToCountry { get; set; }

    public string? ShipToPostalCode { get; set; }

    /// <summary>The language the documents are written in (<c>nl</c>, <c>fr</c>); null for the default.</summary>
    public string? Language { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public static string IdFor(string orderId) => "InvoiceBillingDetails/" + Invoice.Hash(orderId);
}

/// <summary>A VIES answer, cached (§4.8). Id <c>InvoiceVatChecks/{CC}{number}</c>.</summary>
public sealed class InvoiceVatCheck
{
    public string? Id { get; set; }
    public string CountryCode { get; set; } = "";
    public string Number { get; set; } = "";
    public bool Valid { get; set; }
    public string? Name { get; set; }
    public string? Address { get; set; }
    public string? RequestIdentifier { get; set; }
    public DateTimeOffset CheckedAt { get; set; }

    public static string IdFor(string countryCode, string number) => $"InvoiceVatChecks/{countryCode}{number}";
}

/// <summary>
/// A seller's EU-wide distance sales of goods (and telecommunication, broadcasting and electronic services) per
/// calendar year, for the €10,000 threshold of Art. 59c. Updated in the same unit of work that issues the invoice, so
/// every invoice counts exactly once. Id <c>InvoiceDistanceSales/{issuerKey-hash}/{year}</c>.
/// </summary>
public sealed class InvoiceDistanceSales
{
    public string? Id { get; set; }
    public string IssuerKey { get; set; } = "";
    public int Year { get; set; }
    /// <summary>Cumulative net cross-border B2C sales in EUR cents (Art. 59c(1)(c): "total value, exclusive of VAT").</summary>
    public long NetMinorUnits { get; set; }
    /// <summary>When the threshold was first exceeded this year (destination taxation applies from that supply on).</summary>
    public DateTimeOffset? ExceededAt { get; set; }

    public static string IdFor(string issuerKey, int year) => $"InvoiceDistanceSales/{Invoice.Hash(issuerKey)}/{year}";
}

/// <summary>The state of an invoice's e-invoice delivery.</summary>
public enum InvoiceTransmissionStatus
{
    Pending,
    Sent,
    Delivered,
    Failed,
    /// <summary>The buyer cannot receive on Peppol: the PDF is the invoice (mail).</summary>
    NotReachable,
}

/// <summary>The e-invoice delivery of one invoice. Kept apart from <see cref="Invoice"/>, which is never altered.</summary>
public sealed class InvoiceTransmission
{
    public string? Id { get; set; }
    public string InvoiceId { get; set; } = "";
    public string InvoiceNumber { get; set; } = "";
    public string? SellerOrganizationId { get; set; }
    public string Provider { get; set; } = "";
    public InvoiceTransmissionStatus Status { get; set; }
    public string? ReceiverId { get; set; }
    public string? ProviderReference { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    /// <summary>The EN 16931 rules the UBL broke before sending (it is not sent then).</summary>
    public List<string> ValidationErrors { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }

    public static string IdFor(string invoiceId) => "InvoiceTransmissions/" + Invoice.Hash(invoiceId);
}

/// <summary>
/// What has been credited of one invoice, per line (strategy H needs it). Written only in the cluster-wide unit of work
/// that issues a credit note, so two refunds of one invoice never both credit the same cents.
/// </summary>
public sealed class InvoiceCreditState
{
    public string? Id { get; set; }
    public string InvoiceId { get; set; } = "";
    public List<InvoiceCreditedLine> Lines { get; set; } = [];
    public List<string> CreditNoteIds { get; set; } = [];

    public InvoiceCreditedLine Line(int position)
    {
        var line = Lines.FirstOrDefault(l => l.Position == position);
        if (line is null)
            Lines.Add(line = new InvoiceCreditedLine { Position = position });
        return line;
    }

    public static string IdFor(string invoiceId) => "InvoiceCreditStates/" + Invoice.Hash(invoiceId);
}

/// <summary>Credited so far on one invoice line, in minor units.</summary>
public sealed class InvoiceCreditedLine
{
    public int Position { get; set; }
    public long Gross { get; set; }
    public long Vat { get; set; }
}

/// <summary>A seller's invoicing settings beyond its trader data (Commerce's <c>SellerProfile</c>).</summary>
public sealed class SellerInvoicingProfile
{
    public string? Id { get; set; }
    public string OrganizationId { get; set; } = "";
    /// <summary>The seller opted for destination taxation below the threshold (Art. 59c(3)).</summary>
    public bool DestinationTaxationOption { get; set; }
    /// <summary>The seller is registered for the Union OSS scheme.</summary>
    public bool OssRegistered { get; set; }
    /// <summary>GC8: a non-EU seller's IOSS number (Art. 369q).</summary>
    public string? IossNumber { get; set; }
    /// <summary>Overrides the trader data's country (the establishment for VAT).</summary>
    public string? Country { get; set; }
    public string? PeppolId { get; set; }
    public string? Iban { get; set; }
    public string? CompanyId { get; set; }

    public static string IdFor(string organizationId) => "SellerInvoicingProfiles/" + organizationId;
}
