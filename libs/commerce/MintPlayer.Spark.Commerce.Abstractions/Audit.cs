using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Commerce;

/// <summary>
/// The money audit (default 8, §4.15): every money-moving change (orders, refunds, payouts, price changes, fee ledger
/// entries, disputes) is appended to an audit trail in the same session as the change, so the two save or fail
/// together. Entries are never edited; retention follows <see cref="CommerceDocumentTypes.MoneyAudit"/>.
/// </summary>
public interface ICommerceAudit
{
    /// <summary>Appends <paramref name="entry"/> in <paramref name="session"/>. The actor and the time are filled in
    /// from the request and the clock when the entry leaves them empty.</summary>
    Task RecordAsync(Raven.Client.Documents.Session.IAsyncDocumentSession session, CommerceAuditEntry entry, CancellationToken cancellationToken = default);
}

/// <summary>One money audit entry.</summary>
/// <remarks>
/// <see cref="Action"/> uses the constants of <see cref="CommerceAuditActions"/> or a package's own,
/// prefixed with its alias (<c>commerce-payments.payout</c>).
/// </remarks>
public sealed record CommerceAuditEntry
{
    /// <summary>What happened (<see cref="CommerceAuditActions"/>).</summary>
    public required string Action { get; init; }

    /// <summary>The document the change is about (an order, a refund, an item).</summary>
    public required string SubjectId { get; init; }

    /// <summary>The seller organization involved; null for the platform.</summary>
    public string? OrganizationId { get; init; }

    /// <summary>The amount before, when it changed.</summary>
    public Money? Before { get; init; }

    /// <summary>The amount after, or the amount moved.</summary>
    public Money? After { get; init; }

    /// <summary>A short free-text reason (a refund reason, a diminished-value reason).</summary>
    public string? Reason { get; init; }

    /// <summary>The acting user id; filled in from the request when null. Null for system jobs.</summary>
    public string? ActorId { get; init; }

    /// <summary>When; filled in from the clock when null.</summary>
    public DateTimeOffset? At { get; init; }

    /// <summary>Extra key/value facts (provider ids, line keys).</summary>
    public IReadOnlyDictionary<string, string>? Data { get; init; }
}

/// <summary>The audit actions Commerce itself writes.</summary>
public static class CommerceAuditActions
{
    /// <summary>A sellable item's price changed.</summary>
    public const string PriceChanged = "commerce.price-changed";

    /// <summary>A channel fee rule changed (configuration reload).</summary>
    public const string FeeChanged = "commerce.fee-changed";

    /// <summary>A seller's sales were closed or reopened for DAC7 data (GA6, D43).</summary>
    public const string Dac7SalesBlock = "commerce.dac7-sales-block";

    /// <summary>A seller exported buyer or attendee data (GB14). Not a money movement, audited all the same.</summary>
    public const string SellerExport = "commerce.seller-export";

    /// <summary>A seller's trader-traceability data was viewed (the identity document).</summary>
    public const string TraderDataViewed = "commerce.trader-data-viewed";
}

/// <summary>
/// The document types retention is set for (§4.15). Values are permanent configuration keys
/// (<c>Spark:Commerce:Retention:Periods:{type}</c>).
/// </summary>
public static class CommerceDocumentTypes
{
    /// <summary>Orders (and their lines).</summary>
    public const string Orders = "orders";

    /// <summary>Invoices and credit notes: never altered; deleted whole when the floor ends.</summary>
    public const string Invoices = "invoices";

    /// <summary>Payments and disputes.</summary>
    public const string Payments = "payments";

    /// <summary>Attendee answers (GB3).</summary>
    public const string AttendeeAnswers = "attendee-answers";

    /// <summary>Back-in-stock subscriptions (GB11).</summary>
    public const string BackInStockSubscriptions = "back-in-stock";

    /// <summary>Tracking attributions (GC2).</summary>
    public const string TrackingAttributions = "tracking-attributions";

    /// <summary>DSA notices, decisions and statements of reasons (GA5).</summary>
    public const string DsaNotices = "dsa-notices";

    /// <summary>DAC7 seller records (GA6).</summary>
    public const string Dac7Records = "dac7-records";

    /// <summary>Trader-traceability data (GA5), counted from the end of the relationship.</summary>
    public const string TraderData = "trader-data";

    /// <summary>The money audit trail.</summary>
    public const string MoneyAudit = "money-audit";

    /// <summary>The price history (GA3).</summary>
    public const string PriceHistory = "price-history";

    /// <summary>Seller-export audit entries (GB14).</summary>
    public const string SellerExports = "seller-exports";
}

/// <summary>
/// The legal minimum a document type must be kept (§4.15). Commerce ships floors for its known types; a package that
/// owns another type registers an <see cref="IRetentionFloorSource"/>. ⚠️ Floors are taken from the research and are
/// to be confirmed per country (D37); a host's counsel overrides them with
/// <c>Spark:Commerce:Retention:ConfirmedFloors</c>.
/// </summary>
/// <param name="DocumentType">A <see cref="CommerceDocumentTypes"/> value or a package's own.</param>
/// <param name="Floor">The minimum period; <see cref="TimeSpan.Zero"/> for none.</param>
/// <param name="LegalBasis">The cited rule, for the startup message and the guide.</param>
/// <param name="Country">The ISO country the floor is for; null for EU-wide.</param>
/// <param name="Confirmed">Whether the floor was checked against the official text; false shows "confirm per country".</param>
public sealed record RetentionFloor(string DocumentType, TimeSpan Floor, string LegalBasis, string? Country, bool Confirmed);

/// <summary>Contributes retention floors for a package's document types (§4.15).</summary>
public interface IRetentionFloorSource
{
    /// <summary>The floors for <paramref name="country"/> (ISO 3166-1 alpha-2).</summary>
    IEnumerable<RetentionFloor> GetFloors(string country);
}

/// <summary>The effective retention periods (§4.15), floors applied.</summary>
public interface ICommerceRetention
{
    /// <summary>The period <paramref name="documentType"/> is kept: the host's period, never below the floor.</summary>
    TimeSpan GetPeriod(string documentType);

    /// <summary>The floor in force for <paramref name="documentType"/>, or null when the type has none.</summary>
    RetentionFloor? GetFloor(string documentType);
}
