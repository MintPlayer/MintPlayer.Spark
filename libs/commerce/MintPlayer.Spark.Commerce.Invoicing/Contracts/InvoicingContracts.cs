using System.Text.Json.Serialization;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Commerce.Invoicing.Contracts;

/// <summary>What a document is, on the wire.</summary>
[SparkContract]
[JsonConverter(typeof(JsonStringEnumConverter<SparkInvoiceKind>))]
public enum SparkInvoiceKind
{
    [JsonStringEnumMemberName("receipt")] Receipt,
    [JsonStringEnumMemberName("invoice")] Invoice,
    [JsonStringEnumMemberName("creditNote")] CreditNote,
    [JsonStringEnumMemberName("feeInvoice")] FeeInvoice,
    [JsonStringEnumMemberName("feeCreditNote")] FeeCreditNote,
}

/// <summary>One document as <c>GET /spark/commerce/invoicing/documents</c> lists it.</summary>
/// <param name="Id">The document id.</param>
/// <param name="Number">The printed number.</param>
/// <param name="Kind">Receipt, invoice or credit note.</param>
/// <param name="IssueDate">The invoice date, <c>yyyy-MM-dd</c>.</param>
/// <param name="Total">The total with VAT.</param>
/// <param name="Currency">ISO 4217.</param>
/// <param name="CreditedNumber">For a credit note: the document it credits.</param>
/// <param name="PdfUrl">A signed link to the PDF (valid 90 days).</param>
[SparkContract]
public sealed record SparkInvoiceSummary(string Id, string Number, SparkInvoiceKind Kind, string IssueDate, decimal Total, string Currency, string? CreditedNumber, string PdfUrl);

/// <summary>The buyer's billing details for an order (<c>POST /spark/commerce/invoicing/billing</c>).</summary>
/// <param name="OrderNumber">The order's number (the buyer's own order).</param>
/// <param name="InvoiceRequested">A full invoice instead of a receipt.</param>
/// <param name="Name">Company or person name.</param>
/// <param name="VatId">The VAT id with its country prefix; checked against VIES when the invoice is issued.</param>
/// <param name="CompanyId">The company registration number.</param>
/// <param name="Country">ISO 3166-1 alpha-2.</param>
/// <param name="AddressLines">Street lines.</param>
/// <param name="PostalCode">Postal code.</param>
/// <param name="City">City.</param>
/// <param name="PeppolId">The Peppol participant id (<c>0208:0123456789</c>).</param>
/// <param name="Language">The documents' language (<c>nl</c>, <c>fr</c>, …).</param>
[SparkContract]
public sealed record SparkBillingDetailsRequest(
    string OrderNumber,
    bool InvoiceRequested,
    string? Name,
    string? VatId,
    string? CompanyId,
    string? Country,
    IReadOnlyList<string>? AddressLines,
    string? PostalCode,
    string? City,
    string? PeppolId,
    string? Language);

/// <summary>Why billing details were not saved.</summary>
[SparkContract]
[JsonConverter(typeof(JsonStringEnumConverter<SparkBillingDetailsOutcome>))]
public enum SparkBillingDetailsOutcome
{
    [JsonStringEnumMemberName("saved")] Saved,
    [JsonStringEnumMemberName("alreadyInvoiced")] AlreadyInvoiced,
    [JsonStringEnumMemberName("invalidVatId")] InvalidVatId,
}

/// <summary>What <c>POST /spark/commerce/invoicing/billing</c> answers.</summary>
[SparkContract]
public sealed record SparkBillingDetailsResponse(SparkBillingDetailsOutcome Outcome);

/// <summary>A TEDB rate suggestion (GC10): never applied automatically.</summary>
/// <param name="Country">ISO country.</param>
/// <param name="Type">TEDB rate type.</param>
/// <param name="RatePercent">The rate in percent.</param>
/// <param name="Category">TEDB category, or <c>standard</c>.</param>
/// <param name="CategoryDescription">TEDB's description.</param>
/// <param name="SituationOn">The date TEDB stated it for, <c>yyyy-MM-dd</c>.</param>
/// <param name="ConfiguredRatePercent">The host's configured rate for the category named like it, if any (for comparison).</param>
[SparkContract]
public sealed record SparkVatRateSuggestion(string Country, string Type, decimal RatePercent, string Category, string? CategoryDescription, string SituationOn, decimal? ConfiguredRatePercent);
