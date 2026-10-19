using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Commerce.Ticketing.Events.Contracts;

// The TypeScript contracts of @mintplayer/ng-spark/ticketing/events (X20 item 2, docs/guide-typescript-contracts.md).
// Ids travel in bodies and query strings, never in paths (RavenDB ids contain slashes). Enum values travel as their
// names. Item ids (checkout's ItemId) are "ticketing:{phaseId}|{ticketTypeId}|{category}|{dateId}" (or "|pass").

// ---- public event page ---------------------------------------------------------------------------------------------

/// <summary>A published event as its public page shows it (<c>GET /spark/ticketing/events/public?slug=</c>).</summary>
/// <param name="Currency">The currency of every price of the event.</param>
[SparkContract]
public sealed record SparkPublicEvent(
    string Id,
    string Slug,
    TranslatedString Name,
    TranslatedString? Description,
    string? ImageUrl,
    string Status,
    int? MinimumAge,
    IReadOnlyList<string> Performers,
    string? OrganizationId,
    string? OrganizerName,
    string Channel,
    string? Currency,
    IReadOnlyList<SparkPublicDate> Dates,
    IReadOnlyList<SparkPublicTicketType> TicketTypes,
    IReadOnlyList<SparkPublicPhase> Phases);

/// <summary>A date. Times are instants with their stored offset; the page renders them in the viewer's zone with the venue-local hint (<c>VenueTimeZoneId</c>, D33/D35).</summary>
/// <param name="LayoutSnapshotId">A seated date's snapshot: the seat map (<c>ticketing/venues</c>) and its live topic.</param>
[SparkContract]
public sealed record SparkPublicDate(
    string Id,
    TranslatedString? Name,
    DateTimeOffset StartsAt,
    DateTimeOffset? DoorsOpenAt,
    DateTimeOffset? EndsAt,
    string? VenueId,
    string? VenueName,
    IReadOnlyList<string> VenueAddressLines,
    string? VenueTimeZoneId,
    string? LayoutSnapshotId,
    string Status,
    bool OnSale);

/// <summary>A ticket type. <c>Kind</c>: <c>Admission</c>, <c>Pass</c> or <c>AddOn</c> (GB2: bought with one of <c>ParentTypeIds</c>, or any admission).</summary>
/// <param name="AllowedSeatTags">Default 3: the restricting seat tags it may take (1 wheelchair space, 2 companion seat).</param>
[SparkContract]
public sealed record SparkPublicTicketType(
    string Id,
    TranslatedString Name,
    TranslatedString? Description,
    string Kind,
    IReadOnlyList<string> DateIds,
    IReadOnlyList<string> ParentTypeIds,
    int? MinimumAge,
    int AllowedSeatTags,
    bool HasQuestions);

/// <summary>A sale phase. <c>AdmissionScope</c> is the waiting room's gate when <c>Queued</c> (§4.12).</summary>
[SparkContract]
public sealed record SparkPublicPhase(
    string Id,
    TranslatedString Name,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt,
    IReadOnlyList<string> DateIds,
    bool Open,
    bool PresaleCodeRequired,
    bool Queued,
    DateTimeOffset? QueueOpensAt,
    string? AdmissionScope,
    bool PreSelect,
    IReadOnlyList<SparkPublicPrice> Prices);

/// <summary>A price: the unit price and the <c>FinalPrice</c> of one ticket, every mandatory fee included (§4.4, default 4). Only <c>FinalPrice</c> is shown as the price.</summary>
[SparkContract]
public sealed record SparkPublicPrice(string TicketTypeId, string Category, Money Price, Money FinalPrice);

// ---- availability --------------------------------------------------------------------------------------------------

/// <summary>Availability per date (<c>GET /spark/ticketing/events/availability?eventId=&amp;phaseId=</c>), also the waiting page's (§4.11: one room, availability per date).</summary>
[SparkContract]
public sealed record SparkEventAvailability(string EventId, string? PhaseId, IReadOnlyList<SparkDateAvailability> Dates);

/// <summary>
/// One date. <c>Available</c> is null for a seated date (its seat map shows the seats). <c>HoldsOutstanding</c>: sold out
/// now, but places may come back when holds lapse. <c>LiveTopics</c> are the inventory's public topics
/// (<c>commerce-inventory:{quotaId}</c>, X2) to subscribe to for updates.
/// </summary>
[SparkContract]
public sealed record SparkDateAvailability(
    string DateId,
    int? Available,
    bool SoldOut,
    bool HoldsOutstanding,
    IReadOnlyList<string> LiveTopics,
    IReadOnlyList<SparkTypeAvailability> TicketTypes);

/// <summary>A capped ticket type's places left on a date (its own quota, or the phase's).</summary>
[SparkContract]
public sealed record SparkTypeAvailability(string TicketTypeId, int Available);

// ---- one-screen event ----------------------------------------------------------------------------------------------

/// <summary>
/// <c>POST /spark/ticketing/events/quick</c>: a one-off event on one screen with defaults (§4.11): one date, one sale
/// phase (from now, or <c>SaleStartsAt</c>, until the start), the given ticket types and prices, the default channel.
/// </summary>
/// <param name="Capacity">General-admission places; ignored with a <c>LayoutId</c>.</param>
/// <param name="Publish">Publish at once (needs the organization's Publish role, X8).</param>
[SparkContract]
public sealed record SparkQuickEventRequest(
    string OrganizationId,
    TranslatedString Name,
    DateTimeOffset StartsAt,
    IReadOnlyList<SparkQuickTicketType> TicketTypes,
    int Capacity = 0,
    DateTimeOffset? DoorsOpenAt = null,
    DateTimeOffset? EndsAt = null,
    string? VenueId = null,
    string? LayoutId = null,
    DateTimeOffset? SaleStartsAt = null,
    DateTimeOffset? SaleEndsAt = null,
    string? Channel = null,
    TranslatedString? Description = null,
    bool Publish = false);

/// <summary>One ticket type of a one-screen event, with its price (per category for a seated date).</summary>
[SparkContract]
public sealed record SparkQuickTicketType(TranslatedString Name, decimal Price, string? Category = null, int? CapacityPerDate = null);

/// <summary>What <c>POST …/quick</c> created.</summary>
[SparkContract]
public sealed record SparkQuickEventResult(string EventId, string Slug, string DateId, string PhaseId, IReadOnlyList<string> TicketTypeIds, bool Published);

// ---- attendee questions (GB3) --------------------------------------------------------------------------------------

/// <summary>The attendee questions of the caller's own order (<c>GET /spark/ticketing/events/attendees?number=</c>).</summary>
[SparkContract]
public sealed record SparkAttendeeQuestionnaire(string OrderNumber, IReadOnlyList<SparkAttendeeLine> Lines);

/// <summary>An order line with questions: one answer set per ticket position. <c>Open</c> until <c>Deadline</c>.</summary>
[SparkContract]
public sealed record SparkAttendeeLine(
    string LineId,
    TranslatedString? Title,
    string TicketTypeId,
    IReadOnlyList<SparkAttendeeQuestion> Questions,
    IReadOnlyList<SparkAttendeePosition> Positions,
    DateTimeOffset Deadline,
    bool Open);

/// <summary>
/// A question. <c>Kind</c>: <c>Text</c>, <c>MultilineText</c>, <c>Choice</c>, <c>MultipleChoice</c>, <c>Date</c>,
/// <c>Checkbox</c>. <c>AskAt</c>: <c>Checkout</c> (on the order page right after placing) or <c>Later</c>. A sensitive one
/// shows <c>ConsentText</c> with an unticked consent box.
/// </summary>
[SparkContract]
public sealed record SparkAttendeeQuestion(
    string Key,
    TranslatedString Label,
    string Kind,
    IReadOnlyList<SparkAttendeeChoice> Choices,
    bool Required,
    string AskAt,
    bool Sensitive,
    TranslatedString? ConsentText);

[SparkContract]
public sealed record SparkAttendeeChoice(string Key, TranslatedString Label);

/// <summary>One ticket's answers. <c>Missing</c>: required questions not answered yet.</summary>
[SparkContract]
public sealed record SparkAttendeePosition(int Position, IReadOnlyList<SparkAttendeeAnswerView> Answers, IReadOnlyList<string> Missing);

[SparkContract]
public sealed record SparkAttendeeAnswerView(string QuestionKey, string Value, bool Consented);

/// <summary><c>POST /spark/ticketing/events/attendees</c>: the answers of one ticket position. <c>Language</c> is the language the consent texts were shown in.</summary>
[SparkContract]
public sealed record SparkAttendeeAnswersRequest(string OrderNumber, string LineId, int Position, IReadOnlyList<SparkAttendeeAnswerInput> Answers, string? Language = null);

[SparkContract]
public sealed record SparkAttendeeAnswerInput(string QuestionKey, string? Value, bool Consent = false);

/// <summary>A refusal (HTTP 409): <c>Code</c> under <c>ticketingEvents.refusal.</c>, or the per-question <c>Errors</c> under <c>ticketingEvents.answers.</c>.</summary>
[SparkContract]
public sealed record SparkTicketingRefusal(string Code, string? Subject, IReadOnlyList<SparkTicketingFieldError> Errors);

[SparkContract]
public sealed record SparkTicketingFieldError(string Key, string Code);

// ---- PreSelectOrder (D25) ------------------------------------------------------------------------------------------

/// <summary>The caller's pre-selection for a phase (<c>GET /spark/ticketing/events/preselect?phaseId=</c>).</summary>
[SparkContract]
public sealed record SparkPreSelection(string PhaseId, IReadOnlyList<SparkPreSelectLine> Lines, IReadOnlyList<SparkPreSelectApplied> Applied, DateTimeOffset? AppliedAt);

/// <summary>A pre-selected line: its alternatives, best first.</summary>
[SparkContract]
public sealed record SparkPreSelectLine(IReadOnlyList<SparkPreSelectChoice> Alternatives);

/// <summary>One alternative: an item, a quantity, and a seating option (<c>chain</c> or <c>block</c>).</summary>
[SparkContract]
public sealed record SparkPreSelectChoice(string ItemId, int Quantity, Dictionary<string, string>? Options = null);

/// <summary>A line that was held: which alternative, how many, in which basket line.</summary>
[SparkContract]
public sealed record SparkPreSelectApplied(int Line, int Alternative, string BasketLineId, int Quantity);

/// <summary><c>POST /spark/ticketing/events/preselect</c>.</summary>
[SparkContract]
public sealed record SparkPreSelectionRequest(string PhaseId, IReadOnlyList<SparkPreSelectLine> Lines);

/// <summary><c>POST …/preselect/apply</c>: at admission, with the waiting room's token.</summary>
[SparkContract]
public sealed record SparkPreSelectApplyRequest(string PhaseId, string? AdmissionToken);

/// <summary><c>POST …/preselect/release</c>: one click gives every held line back.</summary>
[SparkContract]
public sealed record SparkPhaseRequest(string PhaseId);
