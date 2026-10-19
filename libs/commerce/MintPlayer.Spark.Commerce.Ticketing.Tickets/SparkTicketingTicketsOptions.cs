using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Commerce.Ticketing.Tickets;

/// <summary>
/// <c>Spark:Commerce:Ticketing:Tickets</c> (PRD §4.11). Code given to <c>AddTicketingTickets(o => …)</c> runs after the
/// section. Whether an event allows transfer and resale is its per-sale policy within the channel's bounds (D2, D30,
/// <c>TicketSalePolicy</c>); its delivery lead time, named tickets, freeze and PDF template are its
/// <c>TicketEventSettings</c>, within the bounds set here.
/// </summary>
public sealed class SparkTicketingTicketsOptions : ISparkConfigurationSection
{
    public static string SectionName => "Spark:Commerce:Ticketing:Tickets";

    /// <summary>The ticket secrets (default 9, S8).</summary>
    public TicketSecretOptions Secrets { get; set; } = new();

    /// <summary>Delivery of the ticket page and the mailed PDF (D11).</summary>
    public TicketDeliveryOptions Delivery { get; set; } = new();

    /// <summary>Transfer and the face-value resale exchange (D30).</summary>
    public TicketExchangeOptions Exchange { get; set; } = new();

    /// <summary>The door scanner (§4.11 "Scanner PWA").</summary>
    public TicketScannerOptions Scanner { get; set; } = new();

    /// <summary>Disputed orders (GB1).</summary>
    public TicketDisputeOptions Disputes { get; set; } = new();

    /// <summary>
    /// How long after an event's last date the holder's name and e-mail address stay on its tickets. Default 30 days,
    /// the same as the attendee answers (§4.11 GB3); the order keeps the buyer's data under its own retention (§4.8).
    /// </summary>
    public TimeSpan HolderRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>The holder-retention job's schedule: six fields = seconds (NCrontab, UTC). Default hourly. Read at registration.</summary>
    public string HolderRetentionSchedule { get; set; } = "0 15 * * * *";
}

/// <summary>
/// The ticket secret: 128 random bits, stored as PBKDF2-HMAC-SHA256 (§4.11, default 9). S8 measured 7.3 ms on the
/// server and 10.9 ms in WebCrypto for 50,000 iterations (PRD §13 round 3) and chose that count.
/// </summary>
public sealed class TicketSecretOptions
{
    /// <summary>PBKDF2 iterations of a newly issued secret. Default 50,000 (S8). A changed value applies to new and reissued tickets; stored ones keep theirs.</summary>
    public int Iterations { get; set; } = 50_000;
}

/// <summary>Delivery (D11): the ticket page and the mail with the PDF appear at the seller's lead time.</summary>
public sealed class TicketDeliveryOptions
{
    /// <summary>The SPA route of a ticket page, with <c>{code}</c>; the secret travels in the fragment (<c>#k=</c>), never to a server log. Default <c>/tickets/{code}</c>.</summary>
    public string TicketPagePath { get; set; } = "/tickets/{code}";

    /// <summary>The SPA route of a transfer acceptance, with <c>{id}</c>; the token travels in the fragment. Default <c>/tickets/transfer/{id}</c>.</summary>
    public string TransferPagePath { get; set; } = "/tickets/transfer/{id}";

    /// <summary>The longest lead time a seller may choose (tickets delivered at most this long before the date). Default 60 days.</summary>
    public TimeSpan MaxLeadTime { get; set; } = TimeSpan.FromDays(60);

    /// <summary>The delivery job's schedule (six fields, seconds first, UTC). Default every minute. Read at registration.</summary>
    public string Schedule { get; set; } = "0 * * * * *";

    /// <summary>The MailManager template of the delivery mail. Default <c>SparkTickets/TicketDelivery</c>.</summary>
    public string MailTemplate { get; set; } = "SparkTickets/TicketDelivery";

    /// <summary>The MailManager template of a transfer offer. Default <c>SparkTickets/TransferOffer</c>.</summary>
    public string TransferMailTemplate { get; set; } = "SparkTickets/TransferOffer";

    /// <summary>The largest logo or background image a ticket template accepts (JPEG). Default 512 KiB.</summary>
    public int MaxTemplateImageBytes { get; set; } = 512 * 1024;

    /// <summary>The most <c>.ics</c> files one delivery mail attaches (one per date). Default 8.</summary>
    public int MaxCalendarAttachments { get; set; } = 8;
}

/// <summary>Transfer and resale (D30).</summary>
public sealed class TicketExchangeOptions
{
    /// <summary>The freeze before doors open when an event sets none: no transfer or resale from then on (§4.11). Default 2 hours.</summary>
    public TimeSpan DefaultFreezeBeforeDoors { get; set; } = TimeSpan.FromHours(2);

    /// <summary>How long a transfer offer may be accepted. Default 7 days (and never after the freeze).</summary>
    public TimeSpan TransferOfferLifetime { get; set; } = TimeSpan.FromDays(7);

    /// <summary>The most a seller may charge for a transfer (a name change), as a share of the ticket's FinalPrice, in basis points. Default 1,000 (10 %).</summary>
    public int MaxTransferFeeBasisPoints { get; set; } = 1_000;
}

/// <summary>The door scanner (§4.11).</summary>
public sealed class TicketScannerOptions
{
    /// <summary>The custom organization role of door staff (X8). Default <c>scanner</c>.</summary>
    public string Role { get; set; } = "scanner";

    /// <summary>How long after the event's last date a device token stays valid; its offline list must be wiped by then. Default 12 hours.</summary>
    public TimeSpan TokenGraceAfterEvent { get; set; } = TimeSpan.FromHours(12);

    /// <summary>The most tickets one page of the offline list holds. Default 5,000 (S9: a 50k list is ten pages).</summary>
    public int ListPageSize { get; set; } = 5_000;

    /// <summary>The most scans one sync carries. Default 2,000.</summary>
    public int MaxScansPerSync { get; set; } = 2_000;

    /// <summary>How far a device's clock may run ahead of the server before its scans are clamped to "now". Default 5 minutes.</summary>
    public TimeSpan MaxClockSkew { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The most presence events kept per ticket, list and date. Default 200.</summary>
    public int MaxPresenceEvents { get; set; } = 200;
}

/// <summary>Disputed orders (GB1).</summary>
public sealed class TicketDisputeOptions
{
    /// <summary>
    /// How long a provider usually takes to decide a dispute. A seller who chose "revoke when the event comes first"
    /// (<c>TicketEventSettings.RevokeOnDisputeBeforeDecision</c>) revokes a disputed order's tickets at once when the
    /// event starts within this window. Default 75 days (card-scheme response windows; confirm per provider).
    /// </summary>
    public TimeSpan DecisionHorizon { get; set; } = TimeSpan.FromDays(75);
}
