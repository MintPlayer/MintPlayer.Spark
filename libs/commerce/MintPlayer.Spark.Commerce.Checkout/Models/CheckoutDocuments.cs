using System.Security.Cryptography;
using System.Text;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Inventory;

namespace MintPlayer.Spark.Commerce.Checkout.Models;

// Names carry the "Checkout" prefix: the same CLR simple name in two libraries silently shares a collection (S0).

/// <summary>
/// A basket (§4.4): one per account and one per guest visitor (X1), merged on sign-in. <b>Written only in cluster-wide
/// sessions</b> (the visitor merge runs every handler in one cluster-wide transaction, and a cluster-wide transaction
/// refuses a document an ordinary session wrote, S18), so it is no persistent-object type: the endpoints read and write it.
/// </summary>
public sealed class CheckoutBasket
{
    public string? Id { get; set; }

    /// <summary>The account; null for a guest's basket.</summary>
    public string? UserId { get; set; }

    /// <summary>The guest's visitor id (X1); null for an account's basket.</summary>
    public string? VisitorId { get; set; }

    public string? Currency { get; set; }

    public List<CheckoutBasketLine> Lines { get; set; } = [];

    /// <summary>The buyer's choices the pricing steps read (donation, a promotion code).</summary>
    public Dictionary<string, string> Choices { get; set; } = new(StringComparer.Ordinal);

    /// <summary>A guest's e-mail address, given at the last step (the abandoned-basket reminder's recipient, with consent).</summary>
    public string? Email { get; set; }

    /// <summary>The order placed from this basket that is still awaiting payment.</summary>
    public string? PendingOrderId { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>GC1: when the reminder went out; one reminder per basket content.</summary>
    public DateTimeOffset? ReminderSentAt { get; set; }

    /// <summary>The id of an account's basket.</summary>
    public static string IdForUser(string userId) => "CheckoutBaskets/u-" + Hash(userId);

    /// <summary>The id of a guest's basket.</summary>
    public static string IdForVisitor(string visitorId) => "CheckoutBaskets/v-" + Hash(visitorId);

    // Ids hold slashes and arbitrary characters; a hash keeps the basket id stable and safe.
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32];
}

/// <summary>One basket line. A line in a held channel carries its own inventory claim (one claim per line, so each line keeps its channel's rules, D40).</summary>
public sealed class CheckoutBasketLine
{
    public string LineId { get; set; } = "";

    public string ItemId { get; set; } = "";

    public string Channel { get; set; } = "";

    public string? SellerOrganizationId { get; set; }

    public int Quantity { get; set; }

    /// <summary>Explicit seats the buyer chose.</summary>
    public List<string> Units { get; set; } = [];

    /// <summary>Picked seats with their rows.</summary>
    public List<SeatPick> Picks { get; set; } = [];

    public Dictionary<string, string> Options { get; set; } = new(StringComparer.Ordinal);

    /// <summary>GB2: the line this add-on belongs to.</summary>
    public string? ParentLineId { get; set; }

    /// <summary>The inventory claim (HoldFromSelection); null in the other modes until "Pay".</summary>
    public string? ClaimId { get; set; }

    /// <summary>When the hold lapses (the basket's countdown shows the earliest).</summary>
    public DateTimeOffset? HeldUntil { get; set; }

    /// <summary>The seats the claim holds (chain fill chose them).</summary>
    public List<string> HeldUnits { get; set; } = [];

    /// <summary>The admission tokens the line was added with, by scope (§4.6, §4.12): a waiting-room token, a presale code.</summary>
    public Dictionary<string, string> AdmissionTokens { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// D16 + X1: the guest visitor id this line was chosen under, stamped when a guest basket moves onto an account at
    /// sign-in. Every later claim of the line (a quantity change, the re-assert at "Pay", the order's limit lines) keeps
    /// the visitor limit key beside the account's, so signing in never resets what the guest already counted.
    /// </summary>
    public string? LimitVisitorId { get; set; }

    public DateTimeOffset AddedAt { get; set; }
}

/// <summary>The order lifecycle (§4.4). Shipping, tickets and invoices are recipients' business, not states.</summary>
public enum OrderState
{
    /// <summary>At the provider (or awaiting a back-office payment). Held lines are extended to the payment deadline (D3).</summary>
    PaymentPending,
    /// <summary>Paid (or zero total, D6) and every remaining line converted.</summary>
    Confirmed,
    /// <summary>The payment failed or was cancelled; holds released (or returned to the basket while they live).</summary>
    Cancelled,
    /// <summary>The payment window closed without a payment.</summary>
    Expired,
    /// <summary>An authorization was released without capture (a race loser with VoidOrder, a refused card fingerprint).</summary>
    Voided,
    /// <summary>A full refund is underway (late payment that could not re-claim, VoidOrder after capture).</summary>
    Refunding,
    Refunded,
    /// <summary>Part of the order was refunded (a refused line with RefundLine, D40).</summary>
    PartiallyRefunded,
    /// <summary>GB1: a chargeback is open; tickets are frozen by Tickets.</summary>
    Disputed,
    /// <summary>GB1: decided for the merchant; tickets unfrozen.</summary>
    DisputeWon,
    /// <summary>GB1: decided for the buyer; releasable units returned, keyed by the dispute id (I4).</summary>
    DisputeLost,
}

/// <summary>How an order came about (GB7).</summary>
public enum OrderOrigin
{
    /// <summary>The buyer's own checkout.</summary>
    Online,
    /// <summary>A box-office phone order; the buyer pays through a mailed payment link.</summary>
    Phone,
    /// <summary>A box-office order paid outside the provider and marked paid by finance (audited).</summary>
    Manual,
    /// <summary>A complimentary order: zero total, counted against quotas and limits, excluded from revenue.</summary>
    Comp,
    /// <summary>A guest-list entry: like a comp, issued from a named list.</summary>
    GuestList,
}

public enum OrderLineStatus
{
    Active,
    /// <summary>Refused at confirmation (race loser, late payment, D1/D3/D40).</summary>
    Refused,
    /// <summary>Returned in full (refund, dispute lost).</summary>
    Returned,
}

/// <summary>
/// An order (§4.4). Written by ordinary sessions only (never in a cluster-wide one); shown read-only through Spark
/// (<c>QueryRead</c>) to the buyer, the seller's Owners/Admins/finance/box-office, and the platform organization.
/// </summary>
public sealed class CheckoutOrder
{
    public string? Id { get; set; }

    /// <summary>A short human reference shown to the buyer.</summary>
    public string Number { get; set; } = "";

    public OrderState State { get; set; }

    public OrderOrigin Origin { get; set; }

    public string? BuyerUserId { get; set; }

    public string? VisitorId { get; set; }

    public string? BuyerEmail { get; set; }

    public string? BuyerName { get; set; }

    /// <summary>The address the order was placed from: dispute evidence (<c>customer_purchase_ip</c>, GB1). Erased with the order's personal data.</summary>
    public string? PurchaseIp { get; set; }

    public string Currency { get; set; } = "EUR";

    /// <summary>Every seller of the order (row security); empty for the platform's own sale.</summary>
    public List<string> Sellers { get; set; } = [];

    /// <summary>D40/D41: lines of several sellers paid in one split payment; the platform is merchant of record.</summary>
    public bool MixedSellers { get; set; }

    public List<CheckoutOrderLine> Lines { get; set; } = [];

    public List<CheckoutOrderExtra> Extras { get; set; } = [];

    public Money Total { get; set; } = Money.Zero("EUR");

    public Money? AmountRefunded { get; set; }

    /// <summary>GB7: comps and guest lists are excluded from revenue reports.</summary>
    public bool ExcludedFromRevenue { get; set; }

    /// <summary>GB7: the named list a comp or guest-list order was issued from.</summary>
    public string? GuestList { get; set; }

    /// <summary>The payment the order is (being) paid with.</summary>
    public CheckoutOrderPayment? Payment { get; set; }

    /// <summary>Every provider payment ever created for the order (a second one is refunded, research §4).</summary>
    public List<CheckoutOrderPayment> Payments { get; set; } = [];

    /// <summary>D3: the PaymentPending deadline (capped by the shortest MaxPaymentWindow of the held lines).</summary>
    public DateTimeOffset? PaymentPendingUntil { get; set; }

    /// <summary>GA2: the legal documents in force at checkout that the buyer accepted.</summary>
    public List<CheckoutOrderAcceptance> Acceptances { get; set; } = [];

    /// <summary>GB12: the buyer's age attestation.</summary>
    public CheckoutAgeAttestation? AgeAttestation { get; set; }

    /// <summary>GA9: the buyer ticked the soft opt-in for marketing mail.</summary>
    public bool MarketingOptIn { get; set; }

    public List<CheckoutOrderDispute> Disputes { get; set; } = [];

    /// <summary>The state before a dispute opened.</summary>
    public OrderState? StateBeforeDispute { get; set; }

    /// <summary>The decision log (dispute evidence, support): what happened, when, by whom.</summary>
    public List<CheckoutOrderLogEntry> Log { get; set; } = [];

    public CheckoutBackOffice? BackOffice { get; set; }

    /// <summary>GC4: the cover taken out.</summary>
    public string? InsurancePolicyId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ConfirmedAt { get; set; }

    public DateTimeOffset? ConfirmationSentAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public bool HasHeldLines => Lines.Any(l => l.Status == OrderLineStatus.Active && l.Allocation != AllocationMode.OnPayment && l.ClaimId is not null);
}

/// <summary>One order line, with the price it was sold at and the claim that holds its units.</summary>
public sealed class CheckoutOrderLine
{
    public string LineId { get; set; } = "";

    public string ItemId { get; set; } = "";

    public TranslatedString? Title { get; set; }

    public string Channel { get; set; } = "";

    public AllocationMode Allocation { get; set; }

    public string? SellerOrganizationId { get; set; }

    public int Quantity { get; set; }

    public Money UnitPrice { get; set; } = Money.Zero("EUR");

    public Money Net { get; set; } = Money.Zero("EUR");

    public Money Vat { get; set; } = Money.Zero("EUR");

    public int? VatRateBasisPoints { get; set; }

    public string? VatCategory { get; set; }

    public List<CheckoutOrderFee> Fees { get; set; } = [];

    public List<CheckoutOrderFee> Adjustments { get; set; } = [];

    /// <summary>What the line costs before the order-level discount: FinalPrice plus adjustments.</summary>
    public Money Total { get; set; } = Money.Zero("EUR");

    /// <summary>
    /// The line's share of the order-level discounts (S21, <see cref="PricedLine.OrderDiscount"/>; zero or negative):
    /// spread over the lines' goods amounts by largest remainder at pricing, the split Invoicing uses too.
    /// <see cref="Net"/> + <see cref="Vat"/> = <see cref="Total"/> − fees + this share. Read the charged amounts through
    /// <see cref="CheckoutOrderLineAmounts"/>.
    /// </summary>
    public Money OrderDiscount { get; set; } = Money.Zero("EUR");

    public string? ClaimId { get; set; }

    /// <summary>The units the claim holds (seats).</summary>
    public List<string> Units { get; set; } = [];

    /// <summary>The claim shape, kept to re-claim the same units after a late payment (D3) and for OnPayment lines.</summary>
    public CheckoutCapacitySnapshot Capacity { get; set; } = new();

    public List<LimitLine> Limits { get; set; } = [];

    /// <summary>The per-buyer limit's scope and maximum (D16), for the card-fingerprint key known only at payment (D29).</summary>
    public string? LimitScope { get; set; }

    public int? LimitMax { get; set; }

    /// <summary>The basket hold's own deadline: a cancelled payment hands the hold back to the basket while it lives.</summary>
    public DateTimeOffset? BasketHoldUntil { get; set; }

    public string? ParentLineId { get; set; }

    public int? MinimumAge { get; set; }

    /// <summary>GB12: tickets and pickup slips show an "ID check" marker.</summary>
    public bool IdCheckRequired { get; set; }

    public bool IsPhysicalGoods { get; set; }

    public DateTimeOffset? ServiceDate { get; set; }

    public DateTimeOffset? ShippedAt { get; set; }

    public OrderLineStatus Status { get; set; }

    public string? StatusReason { get; set; }
}

/// <summary>
/// What the buyer was charged for an order line (S21): every reader of line amounts (refunds, refused-line refunds,
/// payment splits, invoices, exports) goes through these, so the order-discount share is counted exactly once.
/// </summary>
public static class CheckoutOrderLineAmounts
{
    /// <summary>What the buyer pays for the line: <see cref="CheckoutOrderLine.Total"/> + its order-discount share.</summary>
    public static Money Charged(this CheckoutOrderLine line) => line.Total + line.OrderDiscount;

    /// <summary>The fees of the line (as sold).</summary>
    public static Money FeeTotal(this CheckoutOrderLine line) => Money.Sum(line.Fees.Select(f => f.Amount), line.Total.Currency);

    /// <summary>What the buyer pays for the goods (VAT base): <see cref="Charged"/> − fees = <c>Net + Vat</c>.</summary>
    public static Money ChargedGoods(this CheckoutOrderLine line) => line.Charged() - line.FeeTotal();
}

/// <summary>A fee or adjustment, as sold.</summary>
public sealed class CheckoutOrderFee
{
    public string Code { get; set; } = "";
    public string Kind { get; set; } = "";
    public Money Amount { get; set; } = Money.Zero("EUR");
    public string LabelKey { get; set; } = "";
    public bool Mandatory { get; set; }
}

/// <summary>The claim shape of a line, stored (the inventory records are the engine's own).</summary>
public sealed class CheckoutCapacitySnapshot
{
    public List<ItemLine> Items { get; set; } = [];
    public List<QuotaLine> Quotas { get; set; } = [];
    public List<string> Units { get; set; } = [];
    public List<ChainLine> BestAvailable { get; set; } = [];
    public List<SeatPick> Picks { get; set; } = [];

    public static CheckoutCapacitySnapshot From(CheckoutCapacity c) => new()
    {
        Items = [.. c.Items], Quotas = [.. c.Quotas], Units = [.. c.Units], BestAvailable = [.. c.BestAvailable], Picks = [.. c.Picks],
    };

    public bool IsEmpty => Items.Count == 0 && Quotas.Count == 0 && Units.Count == 0 && BestAvailable.Count == 0 && Picks.Count == 0;
}

/// <summary>An order-level extra (donation, insurance, discount).</summary>
public sealed class CheckoutOrderExtra
{
    public string Code { get; set; } = "";
    public PricedExtraKind Kind { get; set; }
    public Money Amount { get; set; } = Money.Zero("EUR");
    public string LabelKey { get; set; } = "";
    public string Source { get; set; } = "";
    public string? VatCategory { get; set; }
    public string? Reference { get; set; }
}

/// <summary>A provider payment of the order.</summary>
public sealed class CheckoutOrderPayment
{
    /// <summary>The <c>ProviderPayment</c> document id.</summary>
    public string? PaymentId { get; set; }
    public string? Provider { get; set; }
    public string? Method { get; set; }
    public int Attempt { get; set; }
    public string Status { get; set; } = "";
    public Money Amount { get; set; } = Money.Zero("EUR");
    public bool ManualCapture { get; set; }
    public string? CheckoutUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>A second payment for an already paid order: refunded (research §4).</summary>
    public bool Superfluous { get; set; }

    /// <summary>GB7: a supplement for an upgrade's price difference; it never confirms or refunds the order.</summary>
    public bool Supplement { get; set; }
}

/// <summary>GA2: a legal-document version the buyer accepted at checkout.</summary>
public sealed class CheckoutOrderAcceptance
{
    public string DocumentId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? OrganizationId { get; set; }
    public int Version { get; set; }
    public DateTimeOffset EffectiveFrom { get; set; }
    public DateTimeOffset AcceptedAt { get; set; }
}

/// <summary>GB12: "I am at least {MinimumAge}". No identity document is stored.</summary>
public sealed class CheckoutAgeAttestation
{
    public int MinimumAge { get; set; }
    public DateTimeOffset AttestedAt { get; set; }
    /// <summary>Who attested (the buyer, or the box-office user who took the phone order).</summary>
    public string? AttestedBy { get; set; }
}

/// <summary>GB1: a dispute on the order's payment.</summary>
public sealed class CheckoutOrderDispute
{
    public string DisputeId { get; set; } = "";
    public string Status { get; set; } = "";
    public Money Amount { get; set; } = Money.Zero("EUR");
    public DateTimeOffset OpenedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    /// <summary>Whether the lost dispute's units were returned (I4, keyed by the dispute id).</summary>
    public bool Returned { get; set; }
}

/// <summary>One entry of the order's decision log.</summary>
public sealed class CheckoutOrderLogEntry
{
    public DateTimeOffset At { get; set; }
    public string Event { get; set; } = "";
    public string? Detail { get; set; }
    public string? ActorId { get; set; }
}

/// <summary>GB7: who made a back-office order and what they did with it.</summary>
public sealed class CheckoutBackOffice
{
    public string CreatedBy { get; set; } = "";
    public string? Note { get; set; }
    public string? MarkedPaidBy { get; set; }
    public DateTimeOffset? MarkedPaidAt { get; set; }
    public string? PaymentReference { get; set; }
    public DateTimeOffset? PaymentLinkSentAt { get; set; }
    public int ResendCount { get; set; }
}
