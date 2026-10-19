using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Inventory;

namespace MintPlayer.Spark.Commerce.Checkout;

// The three extension points of PRD §4.1's coupling rule. Checkout references none of Promotions, Invoicing, Shipping,
// Fulfilment or Ticketing: they register these (TryAddEnumerable, scoped) and Checkout calls them.

// ---- capacity sources ---------------------------------------------------------------------------------------------

/// <summary>
/// Turns an item id into what checkout sells and what the inventory engine must claim for it (PRD §4.1 "capacity
/// sources"). Shop registers one for products, Ticketing.Events for ticket types (seats included), a host for anything
/// else. Discovered in an application (<c>[SparkDiscoverable]</c>); a library registers its own (scoped,
/// <c>TryAddEnumerable</c>). The first source whose <see cref="Handles"/> answers true resolves the item.
/// </summary>
[SparkDiscoverable]
public interface ICheckoutCapacitySource
{
    /// <summary>Whether this source knows the item (usually by its id prefix, e.g. <c>Products/</c>).</summary>
    bool Handles(string itemId);

    /// <summary>The item as checkout sells it now, or null when it does not exist or is not on sale.</summary>
    Task<CheckoutItem?> ResolveAsync(CheckoutItemRequest request, CancellationToken cancellationToken);
}

/// <summary>What the buyer asked for.</summary>
/// <param name="ItemId">The sellable item (product variant, ticket type, …).</param>
/// <param name="Quantity">How many.</param>
/// <param name="Units">Explicit numbered units (seats) the buyer chose on a seat map.</param>
/// <param name="Picks">Picked seats with their rows, for the single-seat rule (D7).</param>
/// <param name="Options">Free-form choices a source understands (a size, a date id, an attendee-question set).</param>
/// <param name="ParentItemId">For an add-on (GB2): the item of the line it is added to.</param>
public sealed record CheckoutItemRequest(
    string ItemId,
    int Quantity,
    IReadOnlyList<string>? Units = null,
    IReadOnlyList<SeatPick>? Picks = null,
    IReadOnlyDictionary<string, string>? Options = null,
    string? ParentItemId = null);

/// <summary>A sellable item as checkout sells it: price, channel, seller, rules and the capacity it takes.</summary>
public sealed record CheckoutItem : ISellableItem
{
    public required string ItemId { get; init; }

    string? ISellableItem.Id => ItemId;

    /// <summary>The channel (D2): its allocation mode, hold bounds, payment window, sign-in and refund rules apply to the line.</summary>
    public required string Channel { get; init; }

    /// <summary>
    /// D2: this sale's own hold time (a seller's per-sale value the source already clamped, e.g. Ticketing.Events'
    /// <c>EffectiveSalePolicy.Hold</c>); null for the channel's default. Checkout clamps it to the channel's
    /// <c>Hold</c> bounds again, so a source can never exceed them.
    /// </summary>
    public TimeSpan? HoldTime { get; init; }

    /// <summary>The seller organization; null for the platform's own sale.</summary>
    public string? OrganizationId { get; init; }

    /// <summary>The unit price (before mandatory fees, D2's per-sale value already clamped by the source).</summary>
    public required Money Price { get; init; }

    Money? ISellableItem.Price => Price;

    public PriceBasis PriceBasis { get; init; } = PriceBasis.Gross;

    public int? VatRateBasisPoints { get; init; }

    /// <summary>The title shown in the basket and on the order (pre-contract information: the main characteristics, CRD Art. 6(1)(a)).</summary>
    public required TranslatedString Title { get; init; }

    /// <summary>The main characteristics in a sentence or two (CRD Art. 6(1)(a)); shown at the last step.</summary>
    public TranslatedString? Description { get; init; }

    /// <summary>When a service takes place (an event date): dispute evidence (<c>service_date</c>) and the withdrawal exemption of CRD Art. 16(l).</summary>
    public DateTimeOffset? ServiceDate { get; init; }

    /// <summary>GB12: the buyer must attest to be at least this old; tickets and pickup slips carry an ID-check marker.</summary>
    public int? MinimumAge { get; init; }

    /// <summary>Physical goods (shipped): a refund after shipment returns no quota until restock (I4).</summary>
    public bool IsPhysicalGoods { get; init; }

    /// <summary>The scope of the per-buyer limit (a sale or the whole event, D16). Null: <c>channels/{channel}</c>.</summary>
    public string? LimitScope { get; init; }

    /// <summary>The per-buyer limit; null takes the channel's <c>PerBuyerLimit</c>.</summary>
    public int? PerBuyerLimit { get; init; }

    /// <summary>
    /// The admission scopes that gate this item (§4.6, §4.12), combined with AND: a waiting-room scope
    /// (<c>ticketing-phase:{phaseId}</c>, <c>waiting-room:{gate}</c>), a presale or unlock code's scope
    /// (<c>promotions:{scope}</c>), or both, as for a queued presale phase. Lines of a gated item are claimed only when
    /// every scope's <see cref="ICheckoutAdmissionGate"/> accepts the token the buyer presented for that scope. Empty:
    /// the item is not gated.
    /// </summary>
    public IReadOnlyList<string> AdmissionScopes { get; init; } = [];

    /// <summary>GB2 add-ons: the item may be bought only together with a line of one of these items, and is refunded with it.</summary>
    public IReadOnlyList<string> RequiresParentItemIds { get; init; } = [];

    /// <summary>A VAT category for invoicing (Invoicing reads it from the order line).</summary>
    public string? VatCategory { get; init; }

    /// <summary>What the inventory engine claims for the requested quantity. Empty: nothing is counted (an unlimited item).</summary>
    public CheckoutCapacity Capacity { get; init; } = new();

    /// <summary>Whether the requested quantity can be bought by the units in <see cref="CheckoutItemRequest.Units"/> only (a seat map).</summary>
    public bool HasNumberedUnits => Capacity.Units.Count > 0 || Capacity.Picks.Count > 0 || Capacity.BestAvailable.Count > 0;
}

/// <summary>The claim shape of a line (§4.3): items through their quota mappings, raw quotas, seats.</summary>
public sealed record CheckoutCapacity
{
    public IReadOnlyList<ItemLine> Items { get; init; } = [];

    public IReadOnlyList<QuotaLine> Quotas { get; init; } = [];

    public IReadOnlyList<string> Units { get; init; } = [];

    public IReadOnlyList<ChainLine> BestAvailable { get; init; } = [];

    public IReadOnlyList<SeatPick> Picks { get; init; } = [];

    public bool IsEmpty => Items.Count == 0 && Quotas.Count == 0 && Units.Count == 0 && BestAvailable.Count == 0 && Picks.Count == 0;
}

// ---- pricing steps -------------------------------------------------------------------------------------------------

/// <summary>
/// A step of the basket's pricing pipeline (PRD §4.1 "pricing steps"). Checkout prices every line with
/// <see cref="IFinalPriceCalculator"/> first (FinalPrice, §4.4), then runs the steps by <see cref="Order"/>: Promotions'
/// codes (around 100), the donation step (900, GC3), the insurance step (950, GC4). A step adds line adjustments or
/// order extras; it never changes another line's FinalPrice. Discovered (<c>[SparkDiscoverable]</c>); a library
/// registers its own (scoped, <c>TryAddEnumerable</c>). Steps run on every quote, so they must be side-effect free:
/// a step that reserves something (a capacity-blocking code) does so from an <see cref="IOrderEventRecipient"/>.
/// </summary>
[SparkDiscoverable]
public interface ICheckoutPricingStep
{
    /// <summary>Lower runs first.</summary>
    int Order { get; }

    Task ApplyAsync(CheckoutPricingContext context, CancellationToken cancellationToken);
}

/// <summary>What the pricing steps see and change.</summary>
public sealed class CheckoutPricingContext
{
    public required string Currency { get; init; }

    /// <summary>The priced lines, each with its FinalPrice.</summary>
    public required IReadOnlyList<PricedLine> Lines { get; init; }

    /// <summary>Order-level extras: a donation, an insurance cover, an order discount.</summary>
    public List<PricedExtra> Extras { get; } = [];

    /// <summary>The buyer's choices that steps read (<c>donation</c>, <c>insurance</c>, a promotion code, …).</summary>
    public required IReadOnlyDictionary<string, string> Choices { get; init; }

    /// <summary>The signed-in buyer, if any.</summary>
    public string? BuyerUserId { get; init; }

    /// <summary>
    /// A guest's e-mail address when known (given at the last step or kept on the basket), else null. With
    /// <see cref="VisitorId"/> it identifies a guest for once-per-buyer rules; the order stores the same address.
    /// </summary>
    public string? BuyerEmail { get; init; }

    /// <summary>The guest's visitor identity (X1), when not signed in.</summary>
    public string? VisitorId { get; init; }

    /// <summary>Whether this is the final pricing of a placed order (false while quoting the basket).</summary>
    public bool Placing { get; init; }

    /// <summary>The sum of every line total and extra.</summary>
    public Money Total => Money.Sum(Lines.Select(l => l.Total).Concat(Extras.Select(e => e.Amount)), Currency);
}

/// <summary>A basket line with its FinalPrice and the adjustments steps made to it.</summary>
public sealed class PricedLine
{
    public required string LineId { get; init; }

    public required CheckoutItem Item { get; init; }

    public required int Quantity { get; init; }

    /// <summary>FinalPrice (§4.4): unit price, VAT and every mandatory fee. Per-order fees are counted once per channel.</summary>
    public required FinalPrice Price { get; init; }

    public List<PriceAdjustment> Adjustments { get; } = [];

    public Money Total => Price.Total + Money.Sum(Adjustments.Select(a => a.Amount), Price.Total.Currency);

    /// <summary>
    /// The goods' net after every adjustment (S21): set once all pricing steps ran, from <see cref="Total"/> minus the
    /// mandatory fees. Null while steps run (read <see cref="Price"/> there).
    /// </summary>
    public Money? Net { get; internal set; }

    /// <summary>The VAT of the goods after every adjustment (S21), set with <see cref="Net"/>.</summary>
    public Money? Vat { get; internal set; }
}

/// <summary>How Checkout rounds the VAT of adjusted order lines (S21; <c>Spark:Commerce:Checkout:VatRounding</c>).</summary>
public enum CheckoutVatRounding
{
    /// <summary>Default: each adjusted line's VAT is extracted once from its own amount.</summary>
    PerLine,
    /// <summary>VAT is rounded once per rate group and split over the group's lines (largest remainder).</summary>
    PerGroup,
}

/// <summary>A change to a line's total (a discount is negative).</summary>
public sealed record PriceAdjustment(string Code, Money Amount, string LabelKey, string Source);

/// <summary>The kinds of order extras.</summary>
public enum PricedExtraKind
{
    /// <summary>GC3: a donation to the host's beneficiary; refunded only with the whole order.</summary>
    Donation,
    /// <summary>GC4: an optional cancellation cover; the insurer, not the seller, handles claims.</summary>
    Insurance,
    /// <summary>An order-level discount (Promotions).</summary>
    Discount,
    /// <summary>Anything else a step adds.</summary>
    Other,
}

/// <summary>An order-level extra.</summary>
/// <param name="Code">Stable code (<c>donation</c>, <c>insurance</c>, a promotion code).</param>
/// <param name="Kind">What it is.</param>
/// <param name="Amount">Its amount (negative for a discount).</param>
/// <param name="LabelKey">Translation key of its label.</param>
/// <param name="Source">Who added it.</param>
/// <param name="VatCategory">For invoicing; a donation is <c>outside-scope</c> by default.</param>
/// <param name="Reference">The step's own reference (an insurance quote id).</param>
public sealed record PricedExtra(string Code, PricedExtraKind Kind, Money Amount, string LabelKey, string Source, string? VatCategory = null, string? Reference = null);

// ---- order-event recipients ----------------------------------------------------------------------------------------

/// <summary>
/// Hears what happened to an order (PRD §4.1 "order-event recipients"): Tickets issues on <c>Confirmed</c> and freezes
/// on <c>Disputed</c>, Invoicing invoices on <c>Confirmed</c> and credits on refunds, Fulfilment requests shipment,
/// Promotions consumes a code. Discovered (<c>[SparkDiscoverable]</c>); a library registers its own (scoped,
/// <c>TryAddEnumerable</c>). Called after the change is stored; a recipient that throws is logged and the event is
/// recorded as undelivered on the order, so every method must be idempotent (key on the order id and event).
/// </summary>
[SparkDiscoverable]
public interface IOrderEventRecipient
{
    Task OnOrderEventAsync(OrderEvent orderEvent, CancellationToken cancellationToken);
}

/// <summary>One order event.</summary>
/// <param name="Order">The order after the change.</param>
/// <param name="Kind">What happened.</param>
/// <param name="PreviousState">The state before, when it changed.</param>
/// <param name="Reference">The event's own key: a line id, refund reference, dispute id or change id.</param>
public sealed record OrderEvent(CheckoutOrder Order, OrderEventKind Kind, OrderState? PreviousState, string? Reference = null)
{
    /// <summary>M9: on <see cref="OrderEventKind.RefundIssued"/>, the refund line by line (Invoicing's credit-note input, S21 strategy H).</summary>
    public Refunds.OrderRefundIssued? Refund { get; init; }

    /// <summary>M9: on <see cref="OrderEventKind.TicketsRevoked"/>, the units whose tickets must be revoked (Tickets, M15).</summary>
    public Refunds.OrderTicketRevocation? Revocation { get; init; }

    /// <summary>M9: on <see cref="OrderEventKind.ReturnExpected"/>, the goods expected back (Shop's return label, the warehouse's expected-returns).</summary>
    public Refunds.OrderReturnExpected? Return { get; init; }
}

public enum OrderEventKind
{
    Placed,
    Confirmed,
    Cancelled,
    Expired,
    Voided,
    /// <summary>A line was refused at confirmation (an OnPayment race loser, a late payment that could not re-claim; D1, D3, D40).</summary>
    LineRefused,
    RefundRequested,
    Refunded,
    RefundFailed,
    Disputed,
    DisputeWon,
    DisputeLost,
    /// <summary>GB7: staff re-sent the confirmation (and, through Tickets, the tickets).</summary>
    ConfirmationResent,
    /// <summary>GB7: a seat change or upgrade; <see cref="OrderEvent.Reference"/> is the change id.</summary>
    LineChanged,
    /// <summary>A line was marked shipped (Shipping/Fulfilment call <see cref="ISparkCheckout.MarkShippedAsync"/>).</summary>
    LineShipped,
    /// <summary>
    /// M9: a refund was issued (the provider paid it out, or finance settled it by hand). <see cref="OrderEvent.Refund"/>
    /// carries the lines; Invoicing issues exactly one credit note per <see cref="OrderEvent.Reference"/> (the refund id).
    /// </summary>
    RefundIssued,
    /// <summary>M9: units of non-goods lines were returned (refund, cancellation, mass refund); Tickets revokes them, keyed by <see cref="OrderEvent.Reference"/>.</summary>
    TicketsRevoked,
    /// <summary>M9, GA1: the buyer withdrew (unconditional, no approval). <see cref="OrderEvent.Reference"/> is the return id.</summary>
    WithdrawalRequested,
    /// <summary>M9, GA8: goods are expected back (<see cref="OrderEvent.Return"/>, with the reason and default disposition).</summary>
    ReturnExpected,
    /// <summary>M9, GA8: a legal-guarantee claim was opened; <see cref="OrderEvent.Reference"/> is the claim id.</summary>
    GuaranteeClaimOpened,
    /// <summary>M9, GA8: a guarantee claim was resolved (repair, replacement, price reduction or refund).</summary>
    GuaranteeClaimResolved,
}

// ---- waiting-room admission ---------------------------------------------------------------------------------------

/// <summary>
/// The admission check for gated items (§4.4, §4.6, §4.12): <c>TryClaim</c> of a line whose item names
/// <see cref="CheckoutItem.AdmissionScopes"/> requires, for every scope, a token that the gate owning the scope accepts.
/// Implemented outside Checkout, which references none of them: <c>MintPlayer.Spark.Commerce.WaitingRoom</c> for
/// waiting-room scopes, <c>MintPlayer.Spark.Commerce.Promotions</c> for presale and unlock codes (scoped,
/// <c>TryAddEnumerable</c>). A scope no gate owns is refused (fail closed); startup logs when no gate is registered.
/// </summary>
/// <remarks>
/// The gate may consume the token (one admission, one claim) only when <see cref="AdmissionCheck.Consume"/> is set: a
/// quote never consumes. With several scopes, checkout first checks every scope without consuming and consumes only when
/// all admit, so a refused presale code does not spend the waiting-room token. Consuming must be idempotent for the
/// same buyer and item (a retry, a second line of the same admission).
/// </remarks>
[SparkDiscoverable]
public interface ICheckoutAdmissionGate
{
    /// <summary>Whether this gate owns <paramref name="scope"/> (one of an item's <see cref="CheckoutItem.AdmissionScopes"/>).</summary>
    bool Handles(string scope);

    Task<AdmissionDecision> CheckAsync(AdmissionCheck check, CancellationToken cancellationToken);
}

/// <summary>One admission question.</summary>
/// <param name="Scope">One of the item's <see cref="CheckoutItem.AdmissionScopes"/>.</param>
/// <param name="Token">The token the client presented for this scope (a waiting-room admission token, a presale code), or null.</param>
/// <param name="UserId">The signed-in buyer.</param>
/// <param name="VisitorId">The guest's visitor id (X1).</param>
/// <param name="ItemId">The item.</param>
/// <param name="Quantity">The quantity claimed.</param>
/// <param name="Consume">Whether this claim uses the admission up (a hold or a placement), as opposed to a re-check.</param>
public sealed record AdmissionCheck(string Scope, string? Token, string? UserId, string? VisitorId, string ItemId, int Quantity, bool Consume);

/// <summary>The gate's answer.</summary>
public sealed record AdmissionDecision(bool Admitted, string? Reason = null)
{
    public static AdmissionDecision Admit { get; } = new(true);

    public static AdmissionDecision Refuse(string reason) => new(false, reason);
}

// ---- insurance (GC4) ------------------------------------------------------------------------------------------------

/// <summary>
/// A ticket-insurance adapter (GC4): offers a cancellation cover as an optional line. The insurer, not the seller,
/// handles claims. Registered by an adapter package (<c>AddTestInsurance()</c> in <c>MintPlayer.Spark.Commerce.Checkout.Test</c>);
/// <c>Spark:Commerce:Checkout:Insurance:Provider</c> picks one when several are registered. Never pre-ticked and shown
/// only after the main price (UCPD Directive 2005/29/EC Annex I point 29 / Art. 6–7 and CRD Art. 22 on pre-ticked
/// boxes: an additional payment needs the consumer's express consent; ⚠️ confirm against EUR-Lex, D37).
/// </summary>
public interface IInsuranceProvider
{
    string Name { get; }

    /// <summary>A cover offer for the basket, or null when none is offered (an excluded item, an unsupported currency).</summary>
    Task<InsuranceQuote?> QuoteAsync(InsuranceQuoteRequest request, CancellationToken cancellationToken);

    /// <summary>Takes out the cover once the order is confirmed. Idempotent by <see cref="InsuranceBindRequest.OrderId"/>.</summary>
    Task<InsurancePolicy> BindAsync(InsuranceBindRequest request, CancellationToken cancellationToken);

    /// <summary>Cancels a cover (the order was voided or refunded in full before the event). Idempotent.</summary>
    Task CancelAsync(string policyId, CancellationToken cancellationToken);
}

/// <summary>The basket an insurance quote is for.</summary>
public sealed record InsuranceQuoteRequest(string Currency, Money InsuredAmount, IReadOnlyList<InsuranceQuoteLine> Lines);

/// <summary>One insured line.</summary>
public sealed record InsuranceQuoteLine(string ItemId, string Channel, int Quantity, Money Total, DateTimeOffset? ServiceDate);

/// <summary>An offer: <see cref="Premium"/> for covering <see cref="InsuredAmount"/>, valid until <see cref="ValidUntil"/>.</summary>
/// <param name="TermsUrl">The insurer's product information and terms (IDD pre-contract information, the insurer's duty).</param>
public sealed record InsuranceQuote(string Provider, string QuoteId, Money Premium, Money InsuredAmount, DateTimeOffset ValidUntil, string? TermsUrl, TranslatedString? Description);

/// <summary>Bind a quote for a confirmed order.</summary>
public sealed record InsuranceBindRequest(string QuoteId, string OrderId, string? BuyerEmail, string? BuyerName, Money Premium);

/// <summary>A cover taken out.</summary>
public sealed record InsurancePolicy(string Provider, string PolicyId, DateTimeOffset BoundAt);
