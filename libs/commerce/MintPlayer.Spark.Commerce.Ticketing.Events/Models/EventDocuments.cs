using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Organizations;
using MintPlayer.Spark.WaitingRoom;

namespace MintPlayer.Spark.Commerce.Ticketing.Events.Models;

// The event model of PRD §4.11 (D17): Event → Dates → Sale phases, with ticket types beside the dates. Each is its own
// document and persistent-object type, owned by the seller's organization (X8): the organization's members edit them,
// and the event alone is published. Dates, ticket types and phases copy the event's OrganizationId on every save
// (EventChildInterceptor), so the organization row rule needs no join.

/// <summary>An event (PRD §4.11): what is sold, by whom, in which channel and under which per-sale policy (D2).</summary>
/// <remarks>
/// <para><b>Publishable (X8).</b> While <see cref="IsPublished"/> is true, everyone may list and read it, and its sale
/// phases sell. Publishing needs the <c>Publish</c> role (Admin) and is refused while the event cannot sell: no date, no
/// ticket type, no priced phase, an unknown channel, a seller that may not sell in it (D21, D43), or a required legal
/// document missing (GA2). Publishing provisions every date's quotas and freezes the layout snapshot of a seated
/// date.</para>
/// <para>Implements <see cref="IOrganizationPublishable"/>; <see cref="OrganizationId"/> is the seller.</para>
/// </remarks>
[RequiresOrganizationRole("Publish", OrganizationRole.Admin)]
public sealed class TicketEvent : IOrganizationPublishable
{
    public string? Id { get; set; }

    /// <summary>The seller organization.</summary>
    public string? OrganizationId { get; set; }

    /// <summary>Whether the event is on sale and its page public (X8).</summary>
    public bool IsPublished { get; set; }

    public TranslatedString Name { get; set; } = new();

    /// <summary>The public page's URL segment (<c>/events/{slug}</c>): lower-case letters, digits and dashes, unique. Set from the name when empty.</summary>
    public string Slug { get; set; } = "";

    public TranslatedString? Description { get; set; }

    /// <summary>An image for the page, the link preview and the schema.org Event (absolute URL or a path on the public origin).</summary>
    public string? ImageUrl { get; set; }

    /// <summary>The channel (<c>Spark:Commerce:Channels:{name}</c>, D2) every ticket of the event sells in. Permanent once sold.</summary>
    public string Channel { get; set; } = "";

    public ETicketEventStatus Status { get; set; } = ETicketEventStatus.Scheduled;

    /// <summary>GB12: buyers attest to be at least this old; a ticket type may raise it.</summary>
    public int? MinimumAge { get; set; }

    /// <summary>The performers (schema.org <c>performer</c>).</summary>
    public List<string> Performers { get; set; } = [];

    /// <summary>The per-sale values the seller chose (D2), clamped to the channel's bounds when read.</summary>
    public TicketSalePolicy Policy { get; set; } = new();

    /// <summary>GB3: days after the last date that attendee answers are kept; null takes <c>Answers:DefaultRetention</c>.</summary>
    public int? AnswerRetentionDays { get; set; }

    /// <summary>GB3: hours before a date's start that answers close; null takes <c>Answers:DefaultDeadlineBeforeStart</c>.</summary>
    public int? AnswerDeadlineHours { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// The per-sale values a seller chooses for an event (D2), each inside its channel's bounds. Stored as chosen and clamped
/// when read (<see cref="Sales.SalePolicies.Effective"/>), so tightening the configuration applies at once.
/// </summary>
public sealed class TicketSalePolicy
{
    /// <summary>How long a hold lasts, in minutes (D1); null takes the channel's default.</summary>
    public int? HoldMinutes { get; set; }

    /// <summary>The most tickets one buyer may hold or buy (D16); never above the channel's limit.</summary>
    public int? PerBuyerLimit { get; set; }

    /// <summary>Whether the per-buyer limit counts per sale phase or across the whole event (D16).</summary>
    public ETicketLimitScope LimitScope { get; set; } = ETicketLimitScope.Event;

    /// <summary>The refund policy (D20); must be one the channel allows.</summary>
    public RefundPolicyKind? RefundPolicy { get; set; }

    /// <summary>For <see cref="RefundPolicyKind.UntilDaysBefore"/>: the last day a refund is possible.</summary>
    public int? RefundDaysBefore { get; set; }

    /// <summary>For <see cref="RefundPolicyKind.WithFee"/>: the fee in basis points of the price.</summary>
    public int? RefundFeeBasisPoints { get; set; }

    /// <summary>Whether tickets may be transferred (D30); only when the channel allows it.</summary>
    public bool Transfer { get; set; }

    /// <summary>Whether the official face-value resale is open (D30); only when the channel allows it.</summary>
    public bool FaceValueResale { get; set; }

    /// <summary>The waiting-room mode of gated phases (D10); one the channel allows.</summary>
    public WaitingRoomMode? QueueMode { get; set; }

    /// <summary>The single-empty-seat rule of pick your seat (D7); null takes the inventory engine's default.</summary>
    public SingleSeatRule? SingleSeatRule { get; set; }
}

/// <summary>A date of an event (PRD §4.11): its own layout snapshot, quotas, doors-open time and start time.</summary>
public sealed class TicketEventDate : IOrganizationOwned
{
    public string? Id { get; set; }

    /// <summary>Copied from the event on every save.</summary>
    public string? OrganizationId { get; set; }

    public string EventId { get; set; } = "";

    /// <summary>An optional label (<c>Day 1</c>, <c>Matinee</c>).</summary>
    public TranslatedString? Name { get; set; }

    /// <summary>When the date starts (stored with its offset, §4.17). Shown in the viewer's zone, with the venue-local hint.</summary>
    public DateTimeOffset StartsAt { get; set; }

    public DateTimeOffset? DoorsOpenAt { get; set; }

    public DateTimeOffset? EndsAt { get; set; }

    /// <summary>The venue (<c>Ticketing.Venues</c>); its address and zone fill the page, the JSON-LD and the <c>.ics</c>.</summary>
    public string? VenueId { get; set; }

    /// <summary>The venue's IANA zone (D33, X16 <c>zoneFrom</c>): the secondary "20:00 in Brussels" label. Filled from the venue or its snapshot.</summary>
    public string? VenueTimeZoneId { get; set; }

    /// <summary>A seated date's layout (<c>VenueLayouts/…</c>). Null for general admission sold from <see cref="Capacity"/>.</summary>
    public string? LayoutId { get; set; }

    /// <summary>The layout version to freeze; null takes the latest published one at publication.</summary>
    public int? LayoutVersion { get; set; }

    /// <summary>The frozen snapshot (§4.11 "a published date freezes a layout snapshot"). Set at publication, never changed after.</summary>
    public string? LayoutSnapshotId { get; set; }

    /// <summary>General admission places of an unseated date. Ignored for a seated date (the snapshot's seats and blocks are its capacity).</summary>
    public int Capacity { get; set; }

    public ETicketEventStatus Status { get; set; } = ETicketEventStatus.Scheduled;

    /// <summary>The start before a reschedule (schema.org <c>previousStartDate</c>).</summary>
    public DateTimeOffset? PreviousStartsAt { get; set; }

    /// <summary>When the date's quotas were provisioned; null while it was never published.</summary>
    public DateTimeOffset? ProvisionedAt { get; set; }

    public int SortOrder { get; set; }
}

/// <summary>A ticket type of an event: what a ticket admits to, its rules and its attendee questions.</summary>
public sealed class TicketType : IOrganizationOwned
{
    public string? Id { get; set; }

    /// <summary>Copied from the event on every save.</summary>
    public string? OrganizationId { get; set; }

    public string EventId { get; set; } = "";

    public TranslatedString Name { get; set; } = new();

    public TranslatedString? Description { get; set; }

    public ETicketTypeKind Kind { get; set; } = ETicketTypeKind.Admission;

    /// <summary>
    /// <see cref="ETicketTypeKind.Admission"/> and <see cref="ETicketTypeKind.AddOn"/>: the dates it may be bought for
    /// (empty: every date of the event). <see cref="ETicketTypeKind.Pass"/>: the dates the pass is valid on (at least
    /// two); one pass takes one place of each.
    /// </summary>
    public List<string> DateIds { get; set; } = [];

    /// <summary>GB2: for an add-on, the ticket types it may be bought with (empty: any admission or pass of the event).</summary>
    public List<string> ParentTypeIds { get; set; } = [];

    /// <summary>The most of this type sold per date (its own quota); null for no cap beyond the date's capacity. Required for an add-on that is limited (parking places).</summary>
    public int? CapacityPerDate { get; set; }

    /// <summary>GB12: raises the event's minimum age for this type (tickets carry an ID-check marker).</summary>
    public int? MinimumAge { get; set; }

    /// <summary>Default 3: the restricting seat tags this type may take (1 = wheelchair space, 2 = companion seat).</summary>
    public int AllowedSeatTags { get; set; }

    /// <summary>The VAT rate in basis points (600 = 6 %); null leaves the rate to invoicing.</summary>
    public int? VatRateBasisPoints { get; set; }

    /// <summary>GB3: the questions asked per ticket.</summary>
    public List<AttendeeQuestion> Questions { get; set; } = [];

    public int SortOrder { get; set; }
}

/// <summary>
/// A sale phase (D17): its own window, offered ticket types and prices, an optional presale access code, queue settings
/// and the PreSelect switch. A phase that covers several dates has one waiting room (§4.11).
/// </summary>
public sealed class TicketSalePhase : IOrganizationOwned
{
    public string? Id { get; set; }

    /// <summary>Copied from the event on every save.</summary>
    public string? OrganizationId { get; set; }

    public string EventId { get; set; } = "";

    public TranslatedString Name { get; set; } = new();

    public DateTimeOffset StartsAt { get; set; }

    /// <summary>The end of the window; null sells until each date starts.</summary>
    public DateTimeOffset? EndsAt { get; set; }

    /// <summary>The dates the phase sells (empty: every date of the event).</summary>
    public List<string> DateIds { get; set; } = [];

    /// <summary>The offered ticket types and their prices per price category.</summary>
    public List<TicketPhasePrice> Prices { get; set; } = [];

    /// <summary>The most tickets the phase sells per date (its own quota, an early-bird allocation); null for no cap.</summary>
    public int? CapacityPerDate { get; set; }

    /// <summary>
    /// Write-only: a presale access code the seller types. The save stores its hash in <see cref="PresaleCodeHash"/> and
    /// clears this field, so the code is never kept in clear.
    /// </summary>
    public string? PresaleCode { get; set; }

    /// <summary>SHA-256 (hex) of the normalised presale code; null for an open phase.</summary>
    public string? PresaleCodeHash { get; set; }

    /// <summary>Whether the phase is gated by a waiting room (§4.12): checkout claims its lines only with an admission token.</summary>
    public bool QueueEnabled { get; set; }

    /// <summary>When the pre-queue opens (the waiting room's schedule); null opens it with the sale.</summary>
    public DateTimeOffset? QueueOpensAt { get; set; }

    /// <summary>D25: buyers may pre-select before admission; the pre-selection is held at admission.</summary>
    public bool PreSelect { get; set; }

    public int SortOrder { get; set; }
}

/// <summary>A ticket type's price in a phase for one price category (<c>""</c>: the only one of an unseated date).</summary>
public sealed class TicketPhasePrice
{
    public string TicketTypeId { get; set; } = "";

    /// <summary>The layout's price category key (<c>LayoutCategory.Key</c>), or empty for every place.</summary>
    public string Category { get; set; } = "";

    /// <summary>The unit price before fees, VAT included (gross).</summary>
    public Money Price { get; set; } = Money.Zero("EUR");
}

/// <summary>GB3: a question asked per ticket.</summary>
public sealed class AttendeeQuestion
{
    /// <summary>Stable key, unique in the ticket type: answers are stored under it.</summary>
    public string Key { get; set; } = "";

    public TranslatedString Label { get; set; } = new();

    public EAttendeeQuestionKind Kind { get; set; } = EAttendeeQuestionKind.Text;

    /// <summary>The options of a choice question.</summary>
    public List<AttendeeQuestionChoice> Choices { get; set; } = [];

    public bool Required { get; set; }

    /// <summary>Asked at checkout, or later on the ticket page until the deadline.</summary>
    public EAttendeeQuestionTiming AskAt { get; set; } = EAttendeeQuestionTiming.Checkout;

    /// <summary>
    /// Special-category data (health, dietary needs revealing religion): answered only with explicit consent to
    /// <see cref="ConsentText"/> (GDPR Art. 9(2)(a), Art. 7(1)), and left out of exports unless
    /// <see cref="IncludeInExport"/>.
    /// </summary>
    public bool Sensitive { get; set; }

    /// <summary>The consent text a sensitive question shows; required when <see cref="Sensitive"/>.</summary>
    public TranslatedString? ConsentText { get; set; }

    /// <summary>Whether a sensitive question's answers go into the seller's export. Default false (§4.11).</summary>
    public bool IncludeInExport { get; set; }
}

/// <summary>An option of a choice question.</summary>
public sealed class AttendeeQuestionChoice
{
    public string Key { get; set; } = "";

    public TranslatedString Label { get; set; } = new();
}

/// <summary>
/// GB3: the answers of one ticket position (order line × position), <c>attendee-answers/{orderId}/{lineId}/{position}</c>.
/// Erased at <see cref="EraseAfter"/> by the retention job, on the buyer's account deletion, and on a transfer or resale
/// (Tickets calls <see cref="Answers.IAttendeeAnswers.EraseAsync"/>, §4.11 "personal data on transfer").
/// </summary>
public sealed class AttendeeAnswerSet
{
    public string? Id { get; set; }

    public string EventId { get; set; } = "";

    /// <summary>The seller; scopes the export (GB14).</summary>
    public string? OrganizationId { get; set; }

    public string OrderId { get; set; } = "";

    public string OrderNumber { get; set; } = "";

    public string LineId { get; set; } = "";

    /// <summary>The ticket's position in the line, from 1.</summary>
    public int Position { get; set; }

    public string TicketTypeId { get; set; } = "";

    public List<string> DateIds { get; set; } = [];

    /// <summary>The buyer's account, when signed in (personal-data export and deletion).</summary>
    public string? BuyerUserId { get; set; }

    public List<AttendeeAnswer> Answers { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>When the retention job erases the set (the event's last date plus its retention).</summary>
    public DateTimeOffset EraseAfter { get; set; }

    public static string IdFor(string orderId, string lineId, int position) => $"attendee-answers/{orderId}/{lineId}/{position}";
}

/// <summary>One answer.</summary>
public sealed class AttendeeAnswer
{
    public string QuestionKey { get; set; } = "";

    /// <summary>The answer: text, a choice key, an ISO date (<c>yyyy-MM-dd</c>), <c>true</c> for a ticked consent; choice keys joined by <c>,</c> for a multiple choice.</summary>
    public string Value { get; set; } = "";

    public bool Sensitive { get; set; }

    /// <summary>For a sensitive answer: when explicit consent was given (the proof, GDPR Art. 7(1)).</summary>
    public DateTimeOffset? ConsentGivenAt { get; set; }

    /// <summary>For a sensitive answer: SHA-256 of the consent text shown, in the language shown.</summary>
    public string? ConsentTextHash { get; set; }

    public string? ConsentLanguage { get; set; }
}

/// <summary>
/// D25: a buyer's pre-selection for a phase, <c>ticketing-preselections/{phaseId}/{identity hash}</c>. Each line lists
/// ranked alternatives (ticket type × price category or block × quantity); at admission the first one that can be held
/// is held, clamped to what is available and to the buyer's limit.
/// </summary>
public sealed class TicketPreSelection
{
    public string? Id { get; set; }

    public string PhaseId { get; set; } = "";

    public string EventId { get; set; } = "";

    /// <summary>The account or visitor identity (X1) the pre-selection belongs to.</summary>
    public string IdentityKey { get; set; } = "";

    public List<PreSelectLine> Lines { get; set; } = [];

    /// <summary>What the last application held, so one click releases it.</summary>
    public List<PreSelectApplied> Applied { get; set; } = [];

    public DateTimeOffset? AppliedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One pre-selected line: its alternatives in order of preference.</summary>
public sealed class PreSelectLine
{
    public List<PreSelectChoice> Alternatives { get; set; } = [];
}

/// <summary>One alternative: an item (ticket type × category × date) and how many, with the seating option (a chain or block key).</summary>
public sealed class PreSelectChoice
{
    public string ItemId { get; set; } = "";

    public int Quantity { get; set; }

    /// <summary>Checkout options: <c>chain</c> (best available along a chain) or <c>block</c> (a standing block).</summary>
    public Dictionary<string, string> Options { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>A line the application put in the basket.</summary>
public sealed class PreSelectApplied
{
    public int Line { get; set; }

    public int Alternative { get; set; }

    public string BasketLineId { get; set; } = "";

    public int Quantity { get; set; }
}

[SparkLookup("TicketEventStatus")]
public enum ETicketEventStatus
{
    Scheduled,
    Postponed,
    Rescheduled,
    Cancelled,
}

[SparkLookup("TicketTypeKind")]
public enum ETicketTypeKind
{
    /// <summary>Admits to one date (seated or not).</summary>
    Admission,
    /// <summary>A multi-day or combi pass: valid on several dates, taking a place of each (GB3).</summary>
    Pass,
    /// <summary>GB2: parking, camping, lockers, vouchers; sold only with a parent ticket in the same order and refunded with it.</summary>
    AddOn,
}

[SparkLookup("TicketLimitScope")]
public enum ETicketLimitScope
{
    /// <summary>The per-buyer limit counts across every phase of the event.</summary>
    Event,
    /// <summary>The per-buyer limit counts per sale phase.</summary>
    Phase,
}

[SparkLookup("AttendeeQuestionKind")]
public enum EAttendeeQuestionKind
{
    Text,
    MultilineText,
    Choice,
    MultipleChoice,
    Date,
    /// <summary>A checkbox (a required one must be ticked). Files are not supported (§4.11).</summary>
    Checkbox,
}

[SparkLookup("AttendeeQuestionTiming")]
public enum EAttendeeQuestionTiming
{
    /// <summary>Asked on the order page right after the order is placed.</summary>
    Checkout,
    /// <summary>Asked later on the ticket page, until the deadline.</summary>
    Later,
}
