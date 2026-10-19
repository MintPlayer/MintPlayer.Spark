using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Commerce.Invoicing;

/// <summary>
/// <c>Spark:Commerce:Invoicing</c> (commerce PRD §4.8, D19; guide <c>docs/guide-commerce-invoicing.md</c>). Nothing
/// tax-related is hard-coded: categories and rates are the host's, and the defaults below are configuration, never tax
/// advice (§4.8 "Liability").
/// </summary>
/// <example>
/// <code>
/// "Spark": { "Commerce": { "Invoicing": {
///   "Platform": { "Name": "Example BV", "VatId": "BE0123456789", "Country": "BE", "City": "Gent", "PostalCode": "9000",
///                 "AddressLines": [ "Voorbeeldstraat 1" ], "PeppolId": "0208:0123456789" },
///   "VatCategories": {
///     "standard":  { "Rates": { "BE": 2100, "NL": 2100, "FR": 2000, "DE": 1900 } },
///     "admission": { "Kind": "Admission", "Rates": { "BE": 600, "NL": 900 } },
///     "donation":  { "Kind": "OutsideScope", "ExemptionReason": "Donation, no consideration" }
///   },
///   "Peppol": { "Provider": "storecove" }
/// } } }
/// </code>
/// </example>
public sealed class SparkCommerceInvoicingOptions : ISparkConfigurationSection
{
    public static string SectionName => "Spark:Commerce:Invoicing";

    /// <summary>The platform as issuer (D5 <c>Platform</c> mode) and as the issuer of fee invoices to sellers.</summary>
    public InvoiceIssuerOptions Platform { get; set; } = new();

    /// <summary>
    /// The host's VAT categories (§4.8: "defined by the host per country; nothing is hard-coded"). Keys are permanent:
    /// a sellable item's <c>VatCategory</c> names one. A line without a category uses <see cref="DefaultVatCategory"/>.
    /// </summary>
    public Dictionary<string, VatCategoryOptions> VatCategories { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The category of a line that names none. Default <c>standard</c>.</summary>
    public string DefaultVatCategory { get; set; } = "standard";

    /// <summary>The category of platform fees (on lines and on fee invoices). Default <c>standard</c>.</summary>
    public string FeeVatCategory { get; set; } = "standard";

    /// <summary>
    /// How B2B invoices compute VAT (S21, round 3): <see cref="InvoicePricingBasis.Net"/> with VAT rounded once per rate
    /// group is exact against EN 16931 BR-CO-17 on every invoice, so it is the B2B/Peppol default. B2C receipts keep
    /// the order's gross prices (what the buyer saw).
    /// </summary>
    public InvoicePricingBasis BusinessPricingBasis { get; set; } = InvoicePricingBasis.Net;

    /// <summary>The number series per document kind (<c>{prefix}{year}-{sequence}</c>). Prefixes are permanent.</summary>
    public InvoiceSeriesOptions Series { get; set; } = new();

    /// <summary>VIES (§4.8): B2B VAT ids are checked before an invoice applies reverse charge or the intra-Community exemption.</summary>
    public ViesOptions Vies { get; set; } = new();

    /// <summary>The €10,000 EU-wide distance-sales threshold of Directive 2006/112/EC Art. 59c (GA4).</summary>
    public DistanceSalesOptions DistanceSales { get; set; } = new();

    /// <summary>
    /// VAT territories (GA4, Art. 6): territories of a member state treated as outside the EU VAT territory. Seeded from
    /// the reference data's country profiles when <c>UseReferenceDataVat()</c> is called; entries here win (same code).
    /// </summary>
    public List<VatTerritoryOptions> Territories { get; set; } = [];

    /// <summary>B2C buyers get a receipt (a simplified invoice) unless they ask for an invoice. Default <c>true</c>.</summary>
    public bool ReceiptsForConsumers { get; set; } = true;

    /// <summary>E-invoicing (Peppol BIS Billing 3) for B2B invoices (§4.8; mandatory in Belgium since 2026-01-01).</summary>
    public PeppolOptions Peppol { get; set; } = new();

    /// <summary>IOSS (GC8): the platform's IOSS number when it is the deemed supplier (Art. 14a(1)).</summary>
    public string? IossNumber { get; set; }

    /// <summary>The consignment ceiling of IOSS (Art. 369l: intrinsic value not exceeding EUR 150). Minor units of EUR.</summary>
    public long IossCeilingMinorUnits { get; set; } = 15_000;

    /// <summary>The DAC7 report (GA6, Directive (EU) 2021/514).</summary>
    public Dac7ReportOptions Dac7 { get; set; } = new();

    /// <summary>Monthly platform-fee invoices to sellers (§4.8 "The platform also invoices sellers for its fees").</summary>
    public FeeInvoiceOptions FeeInvoices { get; set; } = new();

    /// <summary>The cron schedule of the retention sweep (§4.15: invoices are deleted whole when their floor ends). Default 04:23 daily.</summary>
    public string RetentionSchedule { get; set; } = "23 4 * * *";

    /// <summary>The cron schedule of the e-invoice retry. Default every 10 minutes.</summary>
    public string TransmissionRetrySchedule { get; set; } = "*/10 * * * *";

    /// <summary>
    /// The IANA zone whose calendar dates invoices (BT-2) and numbering years: an invoice issued at 00:30 on
    /// 1 January in Brussels is dated and numbered in the new year. Converted by the core (K10, <c>SparkTimeZones</c>).
    /// Default <c>Europe/Brussels</c>; an unknown id falls back to UTC.
    /// </summary>
    public string TimeZone { get; set; } = "Europe/Brussels";

    /// <summary>The language invoices are written in when the buyer's is unknown. Default <c>en</c>.</summary>
    public string DefaultLanguage { get; set; } = "en";

    /// <summary>The role of a seller organization that may read its invoices and exports, beside Owners and Admins. Default <c>finance</c>.</summary>
    public string FinanceRole { get; set; } = "finance";

    /// <summary>Resolves a category, refusing an unknown name so a typo never silently falls back.</summary>
    public VatCategoryOptions GetCategory(string? name)
    {
        var key = name is { Length: > 0 } ? name : DefaultVatCategory;
        return VatCategories.TryGetValue(key, out var category)
            ? category
            : throw new KeyNotFoundException($"The VAT category '{key}' is not configured under {SectionName}:VatCategories.");
    }
}

/// <summary>An issuer's identity on its invoices (EN 16931 BG-4 Seller).</summary>
public sealed class InvoiceIssuerOptions
{
    public string? Name { get; set; }

    /// <summary>The VAT identifier with its country prefix (BT-31), e.g. <c>BE0123456789</c>.</summary>
    public string? VatId { get; set; }

    /// <summary>The legal registration number (BT-30), e.g. the Belgian enterprise number.</summary>
    public string? CompanyId { get; set; }

    /// <summary>ISO 3166-1 alpha-2 country of establishment.</summary>
    public string Country { get; set; } = "BE";

    public List<string> AddressLines { get; set; } = [];

    public string? PostalCode { get; set; }

    public string? City { get; set; }

    public string? Email { get; set; }

    /// <summary>The account payment by transfer goes to (BT-84), printed on invoices.</summary>
    public string? Iban { get; set; }

    /// <summary>The Peppol participant id as <c>{scheme}:{id}</c> (ISO 6523 ICD, e.g. <c>0208:0123456789</c>).</summary>
    public string? PeppolId { get; set; }

    /// <summary>The issuer is registered for the Union OSS scheme (Art. 369b) in its member state of identification.</summary>
    public bool OssRegistered { get; set; }
}

/// <summary>How a VAT category behaves (UNCL 5305 codes in UBL).</summary>
public enum VatCategoryKind
{
    /// <summary>Standard or reduced rate goods or services (<c>S</c>; <c>Z</c> when the rate is 0).</summary>
    Standard,
    /// <summary>Admission to cultural, artistic, sporting, scientific, educational, entertainment or similar events
    /// (Art. 53 B2B, Art. 54(1) B2C): taxed where the event takes place, never reverse-charged by this library.</summary>
    Admission,
    /// <summary>Exempt (<c>E</c>), with an exemption reason.</summary>
    Exempt,
    /// <summary>Outside the scope of VAT (<c>O</c>): donations, insurance premiums the platform collects for an insurer.</summary>
    OutsideScope,
}

/// <summary>One host-defined VAT category.</summary>
public sealed class VatCategoryOptions
{
    public VatCategoryKind Kind { get; set; } = VatCategoryKind.Standard;

    /// <summary>The rate per member state in basis points (21 % = 2100). A missing country is refused when the line is taxed there.</summary>
    public Dictionary<string, int> Rates { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The line is a service (not goods): B2B services follow Art. 44, B2C services Art. 45 (unless <see cref="VatCategoryKind.Admission"/>).</summary>
    public bool IsService { get; set; }

    /// <summary>For <see cref="VatCategoryKind.Exempt"/> and <see cref="VatCategoryKind.OutsideScope"/>: the reason printed (BT-120).</summary>
    public string? ExemptionReason { get; set; }

    /// <summary>The VATEX code (BT-121), e.g. <c>VATEX-EU-132-1N</c>.</summary>
    public string? ExemptionReasonCode { get; set; }

    /// <summary>Whether this category may count toward a DAC7 "personal service" (Annex V Section I A(9)); goods always count.</summary>
    public bool PersonalService { get; set; }
}

/// <summary>Net or gross pricing of an invoice.</summary>
public enum InvoicePricingBasis
{
    /// <summary>Line amounts include VAT (B2C); VAT is extracted per rate group.</summary>
    Gross,
    /// <summary>Line amounts exclude VAT (B2B, EN 16931); VAT is added per rate group (BR-CO-17 exact).</summary>
    Net,
}

/// <summary>The number series prefixes.</summary>
public sealed class InvoiceSeriesOptions
{
    public string Invoice { get; set; } = "F";
    public string Receipt { get; set; } = "R";
    public string CreditNote { get; set; } = "C";
    public string FeeInvoice { get; set; } = "P";
    public string FeeCreditNote { get; set; } = "PC";

    /// <summary>Digits of the sequence (zero-padded). Default 6.</summary>
    public int Digits { get; set; } = 6;
}

/// <summary>The VIES check (<see href="https://ec.europa.eu/taxation_customs/vies/#/technical-information">technical information</see>).</summary>
public sealed class ViesOptions
{
    /// <summary>Check B2B VAT ids against VIES. Default <c>true</c>. Off, every VAT id counts as unverified (VAT is charged).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The REST endpoint <c>POST {BaseUrl}/check-vat-number</c>. Overridable for tests only.</summary>
    public string BaseUrl { get; set; } = "https://ec.europa.eu/taxation_customs/vies/rest-api";

    /// <summary>How long a positive answer is reused. Default 7 days.</summary>
    public TimeSpan CacheFor { get; set; } = TimeSpan.FromDays(7);

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The requester's own VAT id (gives a consultation number, proof of the check); null for none.</summary>
    public string? RequesterVatId { get; set; }
}

/// <summary>The distance-sales threshold (Art. 59c).</summary>
public sealed class DistanceSalesOptions
{
    /// <summary>EUR 10 000 in cents (Art. 59c(1)(c)).</summary>
    public long ThresholdMinorUnits { get; set; } = 1_000_000;
}

/// <summary>One VAT territory exception (Art. 6, Windsor Framework for <c>XI</c>).</summary>
public sealed class VatTerritoryOptions
{
    /// <summary>The member state.</summary>
    public string Country { get; set; } = "";

    /// <summary>ISO 3166-2 code or slug (<c>ES-CN</c>, <c>FI-01</c>).</summary>
    public string Code { get; set; } = "";

    /// <summary>Postcodes inside the territory, anchored (<c>^35\d{3}$</c>).</summary>
    public string? PostcodePattern { get; set; }

    /// <summary>The territory is outside the EU VAT territory: supplies to it are treated as exports.</summary>
    public bool OutsideVatTerritory { get; set; } = true;

    public string LegalBasis { get; set; } = "";
}

/// <summary>Peppol e-invoicing.</summary>
public sealed class PeppolOptions
{
    /// <summary>The access point adapter (<c>storecove</c>, <c>billit</c>, <c>test</c>); null with one registered.</summary>
    public string? Provider { get; set; }

    /// <summary>Send B2B invoices and credit notes whose buyer has a Peppol id. Default <c>true</c>.</summary>
    public bool SendBusinessInvoices { get; set; } = true;

    /// <summary>Look the buyer up in the Peppol directory (through the access point) when only a VAT id is known. Default <c>true</c>.</summary>
    public bool DiscoverReceivers { get; set; } = true;

    /// <summary>Attempts before a transmission stays failed (and is listed for the finance role). Default 8.</summary>
    public int MaxAttempts { get; set; } = 8;
}

/// <summary>The DAC7 report (GA6).</summary>
public sealed class Dac7ReportOptions
{
    /// <summary>The member state the platform reports to (Annex V Section III A). Default the platform's country.</summary>
    public string? ReportingCountry { get; set; }

    /// <summary>The platform operator's TIN in that member state.</summary>
    public string? PlatformTin { get; set; }

    /// <summary>
    /// Ticket sales count as relevant activities. Default <c>false</c>: ticket sales are flagged "probably out of
    /// scope" (§4.8, D37) until the host's counsel confirms.
    /// </summary>
    public bool TicketSalesInScope { get; set; }

    /// <summary>The channels whose sales are ticket sales (flagged as above).</summary>
    public List<string> TicketChannels { get; set; } = [];
}

/// <summary>Monthly fee invoices.</summary>
public sealed class FeeInvoiceOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Default 03:11 on the 1st: the previous month's fees.</summary>
    public string Schedule { get; set; } = "11 3 1 * *";
}
