using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Ticketing.Events.Models;
using MintPlayer.Spark.Organizations;

namespace MintPlayer.Spark.Commerce.Ticketing.Tickets.Models;

// The ticket model of PRD §4.11 (D11, D30, default 9; round 5: GB1, GB3, GB7, GB12, GC7). Names carry the "Ticket"
// prefix: the same CLR simple name in two libraries silently shares a collection (S0).

/// <summary>
/// One ticket: one unit of a confirmed order line (<c>Tickets/{Code}</c>). Shown read-only to the seller's members
/// (organization row rule, X8) with the <b>current holder only</b> (§4.11 round 5): a transfer or resale overwrites the
/// holder and erases the previous one's data from the ticket. Written only by this package's services, in ordinary
/// sessions with optimistic concurrency.
/// </summary>
/// <remarks>
/// <para><b>The secret (default 9, S8).</b> 128 random bits; the ticket stores only its PBKDF2-HMAC-SHA256 verifier
/// (<see cref="SecretSalt"/>, <see cref="SecretHash"/>, <see cref="SecretIterations"/>), which is also what the offline
/// scanner list carries, so a stolen scanner cannot mint a valid QR code. The secret itself is kept only
/// Data-Protection-encrypted in <see cref="TicketSecret"/> to re-render the ticket page and PDF; it is never part of a
/// persistent object or a list. A reissue (transfer, resale, seat change) draws a new secret and bumps
/// <see cref="Generation"/>, so the previous QR code and ticket link stop working.</para>
/// </remarks>
public sealed class Ticket : IOrganizationOwned
{
    public string? Id { get; set; }

    /// <summary>The public code (16 Crockford base-32 characters): the first half of the QR code and of the ticket link.</summary>
    public string Code { get; set; } = "";

    /// <summary>The seller (the event's organization).</summary>
    public string? OrganizationId { get; set; }

    public string EventId { get; set; } = "";

    /// <summary>The dates the ticket admits to: one, or every date of a multi-day or combi pass (GB3).</summary>
    public List<string> DateIds { get; set; } = [];

    public string TicketTypeId { get; set; } = "";

    public ETicketTypeKind Kind { get; set; }

    /// <summary>The ticket type's name, as sold.</summary>
    public TranslatedString? Title { get; set; }

    /// <summary>The ticketing item it was sold as (<c>ticketing:{phase}|{type}|{category}|{date}</c>).</summary>
    public string ItemId { get; set; } = "";

    /// <summary>The order the current holder's ticket belongs to (the original order, or the resale purchase).</summary>
    public string OrderId { get; set; } = "";

    public string OrderNumber { get; set; } = "";

    public string LineId { get; set; } = "";

    /// <summary>The position in <see cref="LineId"/>, from 1.</summary>
    public int Position { get; set; }

    /// <summary>The order that first sold the unit; never changes (a resale keeps the unit under its claim).</summary>
    public string SourceOrderId { get; set; } = "";

    public string SourceLineId { get; set; } = "";

    public int SourcePosition { get; set; }

    /// <summary>The inventory claim that holds the ticket's unit (the source line's claim).</summary>
    public string? SourceClaimId { get; set; }

    /// <summary>The claim of the holder's own line: the source claim, or the resale purchase's (it carries the holder's per-buyer count, D16).</summary>
    public string? HolderClaimId { get; set; }

    /// <summary>GB7: how the source order came about (comps and guest lists are excluded from revenue reports).</summary>
    public OrderOrigin Origin { get; set; }

    /// <summary>GB7: the named guest list of a guest-list entry.</summary>
    public string? GuestList { get; set; }

    /// <summary>A numbered seat's unit (<c>{snapshotId}/{seatKey}</c>); null for general admission.</summary>
    public string? Unit { get; set; }

    /// <summary>The seat as printed (block, row, seat).</summary>
    public string? SeatLabel { get; set; }

    public ETicketStatus Status { get; set; } = ETicketStatus.Valid;

    /// <summary>Why the ticket is on hold or revoked (a refund id, a dispute id, <c>resale</c>).</summary>
    public string? StatusReason { get; set; }

    /// <summary>The revocations already applied (I4: exactly once per revocation id).</summary>
    public List<string> RevocationIds { get; set; } = [];

    /// <summary>GB1: the disputes that froze the ticket; it is valid again when every one is won.</summary>
    public List<string> HoldDisputeIds { get; set; } = [];

    /// <summary>GB12: the minimum age; the scanner shows an "ID check" banner. No identity document is stored.</summary>
    public int? MinimumAge { get; set; }

    // ---- the current holder (personal data, erased on transfer, resale and after the retention) ----------------

    /// <summary>The holder's name (named tickets, D30).</summary>
    public string? HolderName { get; set; }

    public string? HolderEmail { get; set; }

    public string? HolderUserId { get; set; }

    public string? HolderVisitorId { get; set; }

    /// <summary>When the retention job erases the holder's name and address (the last date plus <c>HolderRetention</c>).</summary>
    public DateTimeOffset? HolderEraseAfter { get; set; }

    // ---- the secret's verifier --------------------------------------------------------------------------------

    /// <summary>The PBKDF2 salt, base64url (16 bytes).</summary>
    public string SecretSalt { get; set; } = "";

    /// <summary>The PBKDF2-HMAC-SHA256 output, base64url (32 bytes).</summary>
    public string SecretHash { get; set; } = "";

    public int SecretIterations { get; set; }

    /// <summary>Bumped on every reissue.</summary>
    public int Generation { get; set; } = 1;

    // ---- delivery (D11) ---------------------------------------------------------------------------------------

    /// <summary>When the ticket page shows the QR code and the mail goes out (the seller's lead time); null: at once.</summary>
    public DateTimeOffset? AvailableFrom { get; set; }

    /// <summary>When the delivery job mails the ticket: <see cref="AvailableFrom"/>, or the issue time.</summary>
    public DateTimeOffset DeliverAt { get; set; }

    /// <summary>Whether the current generation still has to be mailed (set on issue and every reissue).</summary>
    public bool DeliveryDue { get; set; }

    /// <summary>The generation last mailed.</summary>
    public int DeliveredGeneration { get; set; }

    public DateTimeOffset? DeliveredAt { get; set; }

    /// <summary>From this instant on, no transfer or resale (§4.11 "freeze before doors").</summary>
    public DateTimeOffset? FreezeAt { get; set; }

    // ---- exchange (D30) ---------------------------------------------------------------------------------------

    /// <summary>The open resale listing, if any.</summary>
    public string? ResaleListingId { get; set; }

    /// <summary>The pending transfer, if any.</summary>
    public string? TransferId { get; set; }

    /// <summary>How the holder got the ticket: <c>null</c> (bought it), <c>transfer:{id}</c> or <c>resale:{listingId}</c>.</summary>
    public string? AcquiredThrough { get; set; }

    // ---- check-in (GB3) ---------------------------------------------------------------------------------------

    /// <summary>Presence per check-in list and date: the scans, in time order, with their double-entry flags.</summary>
    public List<TicketPresence> Presence { get; set; } = [];

    public DateTimeOffset IssuedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public static string IdFor(string code) => "Tickets/" + code;
}

/// <summary>A ticket's presence on one check-in list and date (GB3).</summary>
public sealed class TicketPresence
{
    public string ListId { get; set; } = "";

    public string DateId { get; set; } = "";

    /// <summary>Whether the last accepted scan was an entry.</summary>
    public bool Inside { get; set; }

    /// <summary>Entries counted (double entries excluded).</summary>
    public int Entries { get; set; }

    public List<TicketPresenceEvent> Events { get; set; } = [];
}

/// <summary>One scan of a ticket on a list and date.</summary>
public sealed class TicketPresenceEvent
{
    /// <summary>The device's own id of the scan (a UUID): a re-sent sync is idempotent.</summary>
    public string ScanId { get; set; } = "";

    public ETicketScanDirection Direction { get; set; }

    /// <summary>When the device scanned it (its clock, clamped to the server's "now" plus the allowed skew).</summary>
    public DateTimeOffset At { get; set; }

    public string? DeviceId { get; set; }

    /// <summary>Scanned without a connection and synced later (S9).</summary>
    public bool Offline { get; set; }

    /// <summary>A second entry without an exit in between (or, without re-entry, any second entry): flagged, never silently accepted.</summary>
    public bool DoubleEntry { get; set; }

    /// <summary>For a double entry: the scan that entered first.</summary>
    public string? ConflictScanId { get; set; }
}

/// <summary>
/// The ticket's secret, Data-Protection-encrypted (<c>TicketSecrets/{Code}</c>): read only to render the ticket page and
/// PDF for its holder. Never a persistent object, never in a list or export.
/// </summary>
public sealed class TicketSecret
{
    public string? Id { get; set; }

    public string Protected { get; set; } = "";

    public int Generation { get; set; }

    public static string IdFor(string code) => "TicketSecrets/" + code;
}

/// <summary>
/// Marks a line as issued (<c>TicketIssuances/{orderId}/{lineId}</c>), created with "must not exist" so issuance happens
/// exactly once even when the order event is delivered twice.
/// </summary>
public sealed class TicketIssuance
{
    public string? Id { get; set; }

    public string OrderId { get; set; } = "";

    public string LineId { get; set; } = "";

    public List<string> Codes { get; set; } = [];

    /// <summary>The seat-change ids already applied to the line (GB7).</summary>
    public List<string> AppliedChanges { get; set; } = [];

    public DateTimeOffset IssuedAt { get; set; }

    public static string IdFor(string orderId, string lineId) => $"TicketIssuances/{orderId}/{lineId}";
}

/// <summary>
/// An event's ticket settings (one per event): the delivery lead time (D11), named tickets and the transfer fee (D30),
/// the freeze before doors, the dispute behaviour (GB1) and the ticket template (GC7). Organization-owned; the seller's
/// members edit it. An event without settings delivers at once, unnamed, with the configured freeze and a plain template.
/// </summary>
public sealed class TicketEventSettings : IOrganizationOwned
{
    public string? Id { get; set; }

    /// <summary>Copied from the event on every save.</summary>
    public string? OrganizationId { get; set; }

    public string EventId { get; set; } = "";

    /// <summary>D11: tickets appear this many hours before their (first) date; null delivers at once.</summary>
    public int? DeliveryLeadTimeHours { get; set; }

    /// <summary>D30: the buyer names every ticket's holder; the name is printed and shown at the door.</summary>
    public bool NamedTickets { get; set; }

    /// <summary>D30: the fee for a transfer, in basis points of the ticket's FinalPrice; 0 or null: free.</summary>
    public int? TransferFeeBasisPoints { get; set; }

    /// <summary>The freeze before doors in minutes; null takes <c>Exchange:DefaultFreezeBeforeDoors</c>.</summary>
    public int? FreezeBeforeDoorsMinutes { get; set; }

    /// <summary>GB1: revoke a disputed order's tickets at once when the event starts before the provider is expected to decide (else they are frozen).</summary>
    public bool RevokeOnDisputeBeforeDecision { get; set; }

    /// <summary>GC7: the accent colour of the ticket (header bar, page), <c>#rrggbb</c>.</summary>
    public string? TemplateAccentColor { get; set; }

    /// <summary>GC7: the colour of the text on the accent, <c>#rrggbb</c>.</summary>
    public string? TemplateAccentTextColor { get; set; }

    /// <summary>GC7: extra text the seller adds (directions, house rules), below the fixed blocks.</summary>
    public TranslatedString? TemplateExtraText { get; set; }

    /// <summary>GC7: a logo (JPEG) is stored as the document's <c>logo.jpg</c> attachment.</summary>
    public bool HasLogo { get; set; }

    /// <summary>GC7: a background (JPEG) is stored as the document's <c>background.jpg</c> attachment, drawn behind the header.</summary>
    public bool HasBackground { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public const string LogoAttachment = "logo.jpg";

    public const string BackgroundAttachment = "background.jpg";
}

/// <summary>
/// A check-in list (GB3): a gate, a day or a zone, with the dates and ticket types it admits. A pass counts one entry per
/// date on each list. With re-entry, a ticket toggles in and out; a second "in" without an "out" is a double entry.
/// Zones (VIP, backstage) are lists that admit only the ticket types named. Offline lists carry the list id, so every
/// device enforces the same rules. An event without lists checks in on one implicit list that admits everything once
/// per date (<see cref="TicketCheckInLists.DefaultListId"/>).
/// </summary>
public sealed class TicketCheckInList : IOrganizationOwned
{
    public string? Id { get; set; }

    /// <summary>Copied from the event on every save.</summary>
    public string? OrganizationId { get; set; }

    public string EventId { get; set; } = "";

    public string Name { get; set; } = "";

    public ETicketCheckInListKind Kind { get; set; } = ETicketCheckInListKind.Gate;

    /// <summary>The dates it admits on (empty: every date of the event).</summary>
    public List<string> DateIds { get; set; } = [];

    /// <summary>The ticket types it admits (empty: every type; a zone must name its types).</summary>
    public List<string> TicketTypeIds { get; set; } = [];

    /// <summary>Re-entry: scans toggle between in and out.</summary>
    public bool AllowReentry { get; set; }

    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; }
}

/// <summary>The implicit list of an event without lists.</summary>
public static class TicketCheckInLists
{
    public const string DefaultPrefix = "default:";

    public static string DefaultListId(string eventId) => DefaultPrefix + eventId;

    public static TicketCheckInList Default(string eventId, string? organizationId) => new()
    {
        Id = DefaultListId(eventId), EventId = eventId, OrganizationId = organizationId, Name = "Entrance",
        Kind = ETicketCheckInListKind.Gate, IsActive = true,
    };
}

/// <summary>
/// A door device (§4.11 "event-scoped device token"): registered by a member holding the <c>scanner</c> role (X8) for
/// one event and list; its token (shown once) is stored as a SHA-256 hash. Valid until the event's last date plus the
/// grace; revoking it makes the device wipe its offline list on its next call.
/// </summary>
public sealed class TicketScannerDevice : IOrganizationOwned
{
    public string? Id { get; set; }

    public string? OrganizationId { get; set; }

    public string EventId { get; set; } = "";

    public string ListId { get; set; } = "";

    /// <summary>The date the device checks in for; null picks the list's date that is on now.</summary>
    public string? DateId { get; set; }

    public string Name { get; set; } = "";

    /// <summary>SHA-256 (hex) of the token.</summary>
    public string TokenHash { get; set; } = "";

    public string CreatedBy { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The token stops working, and the device wipes its list, from here on.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    public bool Revoked { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public DateTimeOffset? LastSeenAt { get; set; }

    public DateTimeOffset? LastSyncAt { get; set; }
}

/// <summary>
/// One scan as the server recorded it (<c>TicketScans/{scanId}</c>): the gate log and the double-entry report (S9).
/// Read-only for the seller's members.
/// </summary>
public sealed class TicketScan : IOrganizationOwned
{
    public string? Id { get; set; }

    public string? OrganizationId { get; set; }

    public string EventId { get; set; } = "";

    public string TicketCode { get; set; } = "";

    public string ListId { get; set; } = "";

    public string DateId { get; set; } = "";

    public string? DeviceId { get; set; }

    public ETicketScanDirection Direction { get; set; }

    /// <summary>The scan's outcome (<see cref="TicketScanResults"/>).</summary>
    public string Result { get; set; } = "";

    public DateTimeOffset At { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }

    public bool Offline { get; set; }

    public bool DoubleEntry { get; set; }

    public string? ConflictScanId { get; set; }

    public static string IdFor(string scanId) => "TicketScans/" + scanId;
}

/// <summary>A transfer offer (D30), <c>TicketTransfers/{id}</c>. Holds the recipient's address only while pending.</summary>
public sealed class TicketTransfer
{
    public string? Id { get; set; }

    public string TicketCode { get; set; } = "";

    public string EventId { get; set; } = "";

    public string? OrganizationId { get; set; }

    /// <summary>Audit (§4.11): the account or visitor that gave the ticket; never the erased answers.</summary>
    public string? FromUserId { get; set; }

    public string? FromVisitorId { get; set; }

    /// <summary>The recipient; erased once the transfer completes or ends.</summary>
    public string? ToEmail { get; set; }

    public string? ToName { get; set; }

    public string TokenHash { get; set; } = "";

    public ETicketTransferStatus Status { get; set; }

    /// <summary>The fee (D30) the giver pays through checkout before the offer goes out; null when free.</summary>
    public Money? Fee { get; set; }

    public string? FeeOrderId { get; set; }

    public string? ToUserId { get; set; }

    public string? ToVisitorId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>
/// A ticket offered on the official face-value resale exchange (D30), <c>TicketResaleListings/{id}</c>. Sold through
/// checkout as the item <c>ticket-resale:{id}</c> over a one-unit quota; the buyer's order reissues the ticket.
/// </summary>
public sealed class TicketResaleListing
{
    public string? Id { get; set; }

    public string TicketCode { get; set; } = "";

    public string EventId { get; set; } = "";

    public string? OrganizationId { get; set; }

    public string Channel { get; set; } = "";

    /// <summary>The asking price before the channel's fees.</summary>
    public Money Price { get; set; } = Money.Zero("EUR");

    /// <summary>The cap: what the seller's ticket cost, as a FinalPrice (§4.4; Belgian Act of 30 July 2013; national text not checked, D37).</summary>
    public Money Cap { get; set; } = Money.Zero("EUR");

    public string? SellerUserId { get; set; }

    public string? SellerVisitorId { get; set; }

    public ETicketResaleStatus Status { get; set; }

    /// <summary>The inventory quota of one unit that makes the listing sell once.</summary>
    public string QuotaId { get; set; } = "";

    public string? BuyerOrderId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>What the previous holder is owed (see <see cref="Exchange.ITicketResaleSettlement"/>).</summary>
    public TicketResaleSettlement? Settlement { get; set; }
}

/// <summary>The previous holder's payout of a sold listing.</summary>
public sealed class TicketResaleSettlement
{
    public string Status { get; set; } = "";

    public Money Amount { get; set; } = Money.Zero("EUR");

    public string OriginalOrderId { get; set; } = "";

    public string OriginalLineId { get; set; } = "";

    public string? Reference { get; set; }

    public DateTimeOffset At { get; set; }
}

[SparkLookup("TicketStatus")]
public enum ETicketStatus
{
    Valid,
    /// <summary>GB1: frozen while a dispute is open; scans as "on hold, see the box office".</summary>
    OnHold,
    Revoked,
}

[SparkLookup("TicketCheckInListKind")]
public enum ETicketCheckInListKind
{
    Gate,
    Day,
    /// <summary>VIP, backstage: admits only the ticket types it names.</summary>
    Zone,
}

[SparkLookup("TicketScanDirection")]
public enum ETicketScanDirection
{
    In,
    Out,
}

[SparkLookup("TicketTransferStatus")]
public enum ETicketTransferStatus
{
    /// <summary>The giver still has to pay the transfer fee.</summary>
    AwaitingFee,
    Pending,
    Accepted,
    Cancelled,
    Expired,
}

[SparkLookup("TicketResaleStatus")]
public enum ETicketResaleStatus
{
    Open,
    Sold,
    Withdrawn,
}

/// <summary>The outcomes of a scan. Permanent: the scanner translates <c>ticketingTickets.scan.{code}</c>.</summary>
public static class TicketScanResults
{
    public const string Admitted = "admitted";
    public const string Exited = "exited";
    /// <summary>A second entry (S9 double entry): the ticket is already inside, or used, on this list and date.</summary>
    public const string AlreadyIn = "alreadyIn";
    /// <summary>An exit of a ticket that is not inside.</summary>
    public const string NotInside = "notInside";
    /// <summary>The list does not admit the ticket type (a zone, GB3).</summary>
    public const string NotOnList = "notOnList";
    /// <summary>The ticket is not valid on this date (or the list does not cover it).</summary>
    public const string WrongDate = "wrongDate";
    public const string Revoked = "revoked";
    /// <summary>GB1: frozen by a dispute.</summary>
    public const string OnHold = "onHold";
    /// <summary>No such ticket, a wrong secret, or another event's ticket.</summary>
    public const string Unknown = "unknown";
    /// <summary>The list does not allow exits (no re-entry).</summary>
    public const string NoReentry = "noReentry";
}
