using MintPlayer.Spark.Abstractions;

[assembly: SparkClientModule("MintPlayer.Spark.Commerce.Contracts", "@mintplayer/ng-spark/commerce/core")]

namespace MintPlayer.Spark.Commerce.Contracts;

// The responses @mintplayer/ng-spark/commerce/core consumes (X20.2). Enum values travel as their names (strings), so
// the contracts stay inside this module.

/// <summary>One line of a price breakdown (a fee).</summary>
[SparkContract]
public sealed record CommerceFeeLine(string Code, string Kind, Money Amount, string LabelKey);

/// <summary>A price as the storefront shows it (default 4, §4.4): the final price with every mandatory fee, the
/// breakdown, and for an announced reduction the Omnibus prior price (GA3).</summary>
/// <param name="UnitTotal">The final price of one unit: the figure to display.</param>
/// <param name="Gross">The goods' price with VAT, before fees.</param>
/// <param name="Vat">The VAT included, when a rate is known.</param>
/// <param name="MandatoryFees">The fees included in <paramref name="UnitTotal"/>.</param>
/// <param name="OptionalFees">Fees offered, not included.</param>
/// <param name="PriorPrice">The lowest final price of the 30 days before the current reduction; null when no reduction is announced.</param>
[SparkContract]
public sealed record CommercePriceDisplay(
    Money UnitTotal,
    Money Gross,
    Money? Vat,
    IReadOnlyList<CommerceFeeLine> MandatoryFees,
    IReadOnlyList<CommerceFeeLine> OptionalFees,
    Money? PriorPrice);

/// <summary>The seller label a listing shows (GA5): trader or not, linked to the trader page.</summary>
/// <param name="OrganizationId">The seller; null for the platform.</param>
/// <param name="Name">The trader's name (traders only).</param>
/// <param name="TraderStatus"><c>Trader</c>, <c>NonTrader</c> or <c>Undeclared</c>.</param>
[SparkContract]
public sealed record CommerceSellerLabel(string? OrganizationId, string? Name, string TraderStatus);

/// <summary>Everything a listing shows about price and seller.</summary>
[SparkContract]
public sealed record CommerceListingInfo(string ItemId, string Channel, CommercePriceDisplay Price, CommerceSellerLabel Seller);

/// <summary>The public trader page (DSA Art. 30(7)): name, address, contact and register, plus the self-certification.
/// Contact fields are null for a non-trader.</summary>
[SparkContract]
public sealed record CommerceTraderPage(
    string OrganizationId,
    string TraderStatus,
    string? Name,
    string? Address,
    string? Country,
    string? Phone,
    string? Email,
    string? TradeRegister,
    string? RegistrationNumber,
    string? VatId,
    bool SelfCertified,
    bool Verified);

/// <summary>One ranking parameter on the generated page (GA5).</summary>
[SparkContract]
public sealed record CommerceRankingParameter(string List, string Parameter, string DescriptionKey, int Order, bool PaidPlacement);

/// <summary>A published legal-document version (GA2).</summary>
[SparkContract]
public sealed record CommerceLegalDocumentView(
    string Id,
    string Kind,
    string? OrganizationId,
    int Version,
    DateTimeOffset EffectiveFrom,
    TranslatedString? Title,
    TranslatedString? Body,
    bool HasPdf);

/// <summary>The seller's own profile, as its admins edit it. Never contains the identity document itself.</summary>
[SparkContract]
public sealed record CommerceSellerProfileView(
    string OrganizationId,
    string Status,
    string? StatusReason,
    string TraderStatus,
    CommerceTraderDataView Trader,
    CommerceDac7View Dac7,
    bool TraderVerified,
    string Kyc,
    bool SalesBlocked,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> SellGateReasons);

/// <summary>The trader data (DSA Art. 30(1)).</summary>
[SparkContract]
public sealed record CommerceTraderDataView(
    string? Name, string? Address, string? Country, string? Phone, string? Email, bool HasIdentityDocument,
    string? PaymentAccount, string? TradeRegister, string? RegistrationNumber, string? VatId, bool SelfCertification);

/// <summary>The DAC7 data (GA6).</summary>
[SparkContract]
public sealed record CommerceDac7View(
    bool IsEntity, string? Tin, string? TinCountry, string? VatId, DateOnly? BirthDate, string? PrimaryAddress,
    string? BusinessRegistrationNumber, string? Iban, string? AccountHolder, int RemindersSent);

/// <summary>The receipt of a DSA notice (Art. 16(4): confirmation of receipt).</summary>
[SparkContract]
public sealed record CommerceNoticeReceipt(string Id, DateTimeOffset ReceivedAt);

/// <summary>The DSA transparency figures for a period (Art. 15(1)(b), 24).</summary>
[SparkContract]
public sealed record CommerceDsaTransparencyReport(
    DateTimeOffset From,
    DateTimeOffset To,
    int NoticesReceived,
    IReadOnlyDictionary<string, int> NoticesByReason,
    int ActionTaken,
    int NoAction,
    int Open,
    int AutomatedDecisions,
    double? MedianHoursToDecision,
    IReadOnlyDictionary<string, int> StatementsByRestriction,
    int OwnInitiativeStatements);
