using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Commerce.Ticketing.Tickets.Contracts;

// The TypeScript contracts of @mintplayer/ng-spark/ticketing/tickets (X20 item 2, docs/guide-typescript-contracts.md).
// Ids travel in bodies and query strings, never in paths (RavenDB ids contain slashes). Enum values travel as their
// names. A ticket's secret travels only in POST bodies (and the ticket link's fragment), never in a query string.

// ---- the holder's ticket page ----------------------------------------------------------------------------------

/// <summary>
/// A ticket as its holder sees it. Before <c>AvailableFrom</c> (the seller's lead time, D11) <c>Payload</c> and
/// <c>QrSvg</c> are null and the page shows "available on …".
/// </summary>
/// <param name="Payload">The QR code's text (<c>ST1.{code}.{secret}</c>); the secret proves possession.</param>
/// <param name="QrSvg">The QR code as an SVG document.</param>
/// <param name="Status"><c>Valid</c>, <c>OnHold</c> (GB1) or <c>Revoked</c>.</param>
/// <param name="MinimumAge">GB12: an ID is checked at the door.</param>
/// <param name="CanTransfer">The event allows transfer (D30), the ticket is valid and the freeze has not begun.</param>
/// <param name="CanResell">The event's face-value resale is open (D30), the ticket is valid and the freeze has not begun.</param>
/// <param name="ResaleCap">What the ticket cost (the resale ceiling, D30), in the currency the event's order was paid in:
/// the resale form's currency before any listing exists. Null when the ticket cannot be resold.</param>
[SparkContract]
public sealed record SparkTicketView(
    string Code,
    int Generation,
    string EventId,
    TranslatedString EventName,
    string? EventSlug,
    string TicketTypeId,
    TranslatedString? TicketTypeName,
    string Kind,
    IReadOnlyList<SparkTicketDate> Dates,
    string? SeatLabel,
    string? HolderName,
    bool NamedTickets,
    string Status,
    int? MinimumAge,
    string OrderNumber,
    bool Available,
    DateTimeOffset? AvailableFrom,
    string? Payload,
    string? QrSvg,
    DateTimeOffset? FreezeAt,
    bool CanTransfer,
    bool CanResell,
    SparkTicketTransferInfo? Transfer,
    SparkTicketResaleInfo? Resale,
    Money? ResaleCap,
    SparkTicketTemplate Template);

/// <summary>A date the ticket admits to; the page renders it in the viewer's zone with the venue-local hint (D33).</summary>
[SparkContract]
public sealed record SparkTicketDate(
    string Id,
    TranslatedString? Name,
    DateTimeOffset StartsAt,
    DateTimeOffset? DoorsOpenAt,
    DateTimeOffset? EndsAt,
    string? VenueName,
    string? VenueTimeZoneId);

/// <summary>A pending transfer. <c>FeeItemId</c> is the checkout item the giver pays first (<c>AwaitingFee</c>).</summary>
[SparkContract]
public sealed record SparkTicketTransferInfo(string TransferId, string Status, DateTimeOffset ExpiresAt, Money? Fee, string? FeeItemId);

/// <summary>An open resale listing of the ticket.</summary>
[SparkContract]
public sealed record SparkTicketResaleInfo(string ListingId, string Status, Money Price, Money Cap);

/// <summary>GC7: the event's ticket template for the page (the PDF uses the same).</summary>
[SparkContract]
public sealed record SparkTicketTemplate(string? AccentColor, string? AccentTextColor, TranslatedString? ExtraText, string? LogoUrl, string? BackgroundUrl);

/// <summary>Tickets of the caller (signed in) or of their own order.</summary>
[SparkContract]
public sealed record SparkTicketList(IReadOnlyList<SparkTicketView> Tickets);

/// <summary>Opens a ticket: its code and, without an account that holds it, the secret from the link's fragment.</summary>
[SparkContract]
public sealed record SparkTicketCredential(string Code, string? Secret = null);

/// <summary>D30: names the holder of a named ticket.</summary>
[SparkContract]
public sealed record SparkTicketHolderRequest(string Code, string Name, string? Secret = null);

/// <summary>D30: offers the ticket to someone else.</summary>
[SparkContract]
public sealed record SparkTicketTransferRequest(string Code, string Email, string? Name = null, string? Secret = null);

/// <summary>What the recipient of a transfer sees before accepting (no personal data of the giver).</summary>
[SparkContract]
public sealed record SparkTicketTransferOffer(
    string TransferId,
    TranslatedString EventName,
    TranslatedString? TicketTypeName,
    IReadOnlyList<SparkTicketDate> Dates,
    string? SeatLabel,
    string Status,
    DateTimeOffset ExpiresAt,
    bool NameRequired);

/// <summary>Accepts a transfer with the token from the offer link's fragment.</summary>
[SparkContract]
public sealed record SparkTicketTransferAccept(string TransferId, string Token, string? Name = null, string? Email = null);

/// <summary>D30: lists the ticket on the face-value resale exchange at <c>Price</c> (before the channel's fees).</summary>
[SparkContract]
public sealed record SparkTicketResaleRequest(string Code, Money Price, string? Secret = null);

/// <summary>An open listing as buyers see it; <c>ItemId</c> goes into checkout's basket.</summary>
/// <param name="FinalPrice">The price a buyer pays, every mandatory fee included (§4.4), never above the original FinalPrice.</param>
[SparkContract]
public sealed record SparkResaleOffer(
    string ListingId,
    string ItemId,
    string TicketTypeId,
    TranslatedString? TicketTypeName,
    IReadOnlyList<string> DateIds,
    string? SeatLabel,
    Money Price,
    Money FinalPrice);

/// <summary>The open listings of an event.</summary>
[SparkContract]
public sealed record SparkResaleOffers(string EventId, IReadOnlyList<SparkResaleOffer> Offers);

/// <summary>A refusal (HTTP 409): a code translated as <c>ticketingTickets.refusal.{code}</c>.</summary>
[SparkContract]
public sealed record SparkTicketRefusal(string Code, string? Subject);

// ---- the seller's back office (GB7) ---------------------------------------------------------------------------

/// <summary>GB7: re-sends the tickets of an order, or one ticket.</summary>
[SparkContract]
public sealed record SparkTicketResendRequest(string? OrderId = null, string? Code = null);

[SparkContract]
public sealed record SparkTicketResendResult(int Sent);

/// <summary>Registers a door device for one event and list (the <c>scanner</c> role, X8).</summary>
/// <param name="ListId">A check-in list of the event; null takes its first list (or the implicit one).</param>
/// <param name="DateId">The date the device checks in for; null picks the date that is on.</param>
[SparkContract]
public sealed record SparkScannerDeviceRequest(string EventId, string Name, string? ListId = null, string? DateId = null);

/// <summary>The device's token, shown once: the device sends it as <c>X-Spark-Scanner: {DeviceKey}.{Token}</c>.</summary>
[SparkContract]
public sealed record SparkScannerDeviceRegistered(string DeviceId, string DeviceKey, string Token, DateTimeOffset ExpiresAt);

[SparkContract]
public sealed record SparkScannerDeviceRevokeRequest(string DeviceId);

// ---- the door scanner (device token) ---------------------------------------------------------------------------

/// <summary>What a device works with: its event, list and dates, the PBKDF2 cost, the freeze and when to wipe.</summary>
/// <param name="FreezeAt">No transfer or resale from then on: a device must sync after it (§4.11).</param>
/// <param name="WipeAfter">The device deletes its offline list and queued scans from then on.</param>
[SparkContract]
public sealed record SparkScannerSession(
    string DeviceId,
    string Name,
    string EventId,
    TranslatedString EventName,
    SparkScannerList List,
    IReadOnlyList<SparkTicketDate> Dates,
    string? DateId,
    int Iterations,
    DateTimeOffset? FreezeAt,
    DateTimeOffset WipeAfter,
    DateTimeOffset ServerTime,
    IReadOnlyList<SparkScannerType> TicketTypes);

/// <summary>A check-in list's rules (GB3), enforced by the device offline exactly as by the server.</summary>
[SparkContract]
public sealed record SparkScannerList(string Id, string Name, string Kind, IReadOnlyList<string> DateIds, IReadOnlyList<string> TicketTypeIds, bool AllowReentry);

[SparkContract]
public sealed record SparkScannerType(string Id, TranslatedString Name);

/// <summary>One ticket of the offline list: the PBKDF2 verifier (never the secret), its rules' inputs and its presence.</summary>
/// <param name="Status"><c>Valid</c>, <c>OnHold</c> or <c>Revoked</c>.</param>
/// <param name="Inside">The dates on which the ticket is inside on the device's list now.</param>
/// <param name="Used">The dates on which the ticket has entered on the device's list at least once.</param>
[SparkContract]
public sealed record SparkScannerEntry(
    string Code,
    string Salt,
    string Hash,
    int Iterations,
    string TicketTypeId,
    IReadOnlyList<string> DateIds,
    string Status,
    int? MinimumAge,
    string? Holder,
    string? Seat,
    IReadOnlyList<string> Inside,
    IReadOnlyList<string> Used);

/// <summary>A page of the offline list (S9). <c>Next</c> continues the download; <c>Cursor</c> asks for changes later.</summary>
[SparkContract]
public sealed record SparkScannerListPage(IReadOnlyList<SparkScannerEntry> Entries, string? Next, string Cursor, DateTimeOffset ServerTime);

/// <summary>An online scan: the QR payload, the direction (<c>In</c>/<c>Out</c>) and the device's scan id.</summary>
[SparkContract]
public sealed record SparkScanRequest(string Payload, string ScanId, string Direction = "In", string? DateId = null);

/// <summary>A scan's outcome (<c>ticketingTickets.scan.{Result}</c>), with what the door staff need to see.</summary>
[SparkContract]
public sealed record SparkScanResult(
    string ScanId,
    string Result,
    string? Code,
    TranslatedString? TicketTypeName,
    string? Seat,
    string? Holder,
    int? MinimumAge,
    bool DoubleEntry,
    string? DateId);

/// <summary>A scan made offline, sent at the next sync. The device verified the secret itself against the list.</summary>
[SparkContract]
public sealed record SparkOfflineScan(string ScanId, string Code, DateTimeOffset At, string Direction = "In", string? DateId = null);

/// <summary>Uploads offline scans and asks for the list's changes since <c>Since</c> (a previous <c>Cursor</c>).</summary>
[SparkContract]
public sealed record SparkScannerSyncRequest(IReadOnlyList<SparkOfflineScan> Scans, string? Since = null);

/// <summary>
/// The live topic <c>ticketing-scanner:{EventId}</c> (X2): the event's tickets changed at <c>ChangedAt</c> (a revocation,
/// a reissue, another device's scan). Public by construction, so it carries no ticket data: a device syncs on it.
/// </summary>
[SparkContract]
public sealed record SparkScannerNudge(string EventId, DateTimeOffset ChangedAt);

/// <summary>
/// The sync's outcome (S9): each uploaded scan's result as the server decided it with every device's scans in time
/// order, the scan ids flagged as double entries (this device's and other devices' alike), and the list's changes.
/// </summary>
[SparkContract]
public sealed record SparkScannerSyncResult(
    IReadOnlyList<SparkScanResult> Results,
    IReadOnlyList<string> DoubleEntries,
    IReadOnlyList<SparkScannerEntry> Changes,
    string Cursor,
    DateTimeOffset ServerTime);
