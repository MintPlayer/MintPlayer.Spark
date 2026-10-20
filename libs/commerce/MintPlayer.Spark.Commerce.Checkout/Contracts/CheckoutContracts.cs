using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Commerce.Checkout.Contracts;

// The TypeScript contracts of @mintplayer/ng-spark/commerce/checkout (X20 item 2, docs/guide-typescript-contracts.md).
// Ids travel in bodies and query strings, never in paths (RavenDB ids contain slashes).

/// <summary>A basket line as the basket page shows it.</summary>
/// <param name="HeldUntil">When its hold lapses (HoldFromSelection); the basket's countdown shows the earliest.</param>
/// <param name="HoldLapsed">The hold lapsed: the line must be re-added (or is re-claimed at "Pay" when still free).</param>
/// <param name="MinimumAge">GB12: shown on the line; the last step asks for the attestation.</param>
/// <param name="UnitTotal">FinalPrice of one unit (every mandatory fee included, §4.4).</param>
[SparkContract]
public sealed record SparkBasketLine(
    string LineId,
    string ItemId,
    TranslatedString? Title,
    string Channel,
    string? SellerOrganizationId,
    int Quantity,
    IReadOnlyList<string> Units,
    DateTimeOffset? HeldUntil,
    bool HoldLapsed,
    int? MinimumAge,
    string? ParentLineId,
    Money? UnitTotal,
    Money? Total);

/// <summary>The current buyer's basket (<c>GET /spark/commerce/checkout/basket</c>).</summary>
/// <param name="EarliestHoldUntil">The earliest hold deadline among the lines (the countdown, D40).</param>
/// <param name="PendingOrderNumber">An order placed from this basket that awaits payment; the basket is locked until it ends.</param>
/// <param name="ExtendableUntil">
/// GA10 (WCAG 2.2.1 "Extend"): the deadline one "More time" (<c>POST …/basket/extend-hold</c>) would give the earliest
/// live hold; null when no hold can move any more (each is at its channel's <c>Hold:Max</c>, or extensions are off).
/// </param>
[SparkContract]
public sealed record SparkBasket(
    IReadOnlyList<SparkBasketLine> Lines,
    string? Currency,
    Money? Total,
    DateTimeOffset? EarliestHoldUntil,
    bool MixedChannels,
    Dictionary<string, string> Choices,
    string? PendingOrderNumber,
    DateTimeOffset? ExtendableUntil);

/// <summary><c>POST /spark/commerce/checkout/basket/add</c>.</summary>
/// <param name="AdmissionTokens">
/// §4.6, §4.12: the admission tokens by scope, one per scope the item is gated by: the waiting room's admission token for
/// <c>ticketing-phase:{phaseId}</c> or <c>waiting-room:{gate}</c>, a presale code for <c>promotions:{scope}</c>.
/// </param>
/// <param name="ParentLineId">GB2: the line an add-on belongs to.</param>
[SparkContract]
public sealed record SparkBasketAddRequest(
    string ItemId,
    int Quantity,
    IReadOnlyList<string>? Units = null,
    Dictionary<string, string>? Options = null,
    string? ParentLineId = null,
    Dictionary<string, string>? AdmissionTokens = null);

/// <summary><c>POST /spark/commerce/checkout/basket/quantity</c>: 0 removes the line.</summary>
[SparkContract]
public sealed record SparkBasketQuantityRequest(string LineId, int Quantity);

/// <summary><c>POST /spark/commerce/checkout/basket/choices</c>: donation, insurance, promotion codes; null removes a choice.</summary>
[SparkContract]
public sealed record SparkBasketChoicesRequest(Dictionary<string, string?> Choices, string? Email = null);

/// <summary>A fee, adjustment or extra as the last step lists it.</summary>
[SparkContract]
public sealed record SparkCheckoutAmount(string Code, string Kind, Money Amount, string LabelKey);

/// <summary>A line of the last step: the main characteristics and the price broken down (CRD Art. 6(1)(a), (e)).</summary>
[SparkContract]
public sealed record SparkCheckoutLine(
    string LineId,
    string ItemId,
    TranslatedString? Title,
    TranslatedString? Description,
    string Channel,
    string? SellerOrganizationId,
    int Quantity,
    IReadOnlyList<string> Units,
    Money UnitPrice,
    Money Net,
    Money Vat,
    IReadOnlyList<SparkCheckoutAmount> Fees,
    IReadOnlyList<SparkCheckoutAmount> Adjustments,
    Money Total,
    int? MinimumAge,
    DateTimeOffset? ServiceDate);

/// <summary>A legal document in force, linked at the last step (GA2); <paramref name="MustAccept"/> ones need the checkbox.</summary>
[SparkContract]
public sealed record SparkCheckoutDocument(string Id, string Kind, string? OrganizationId, int Version, DateTimeOffset EffectiveFrom, TranslatedString? Title, bool HasPdf, bool MustAccept);

/// <summary>A payment method the basket may offer (D3 cap, D40 intersection). Never filtered by country (GA11).</summary>
[SparkContract]
public sealed record SparkCheckoutMethod(string Id, string Provider);

/// <summary>GC3: the donation offer.</summary>
[SparkContract]
public sealed record SparkDonationOffer(string? Beneficiary, IReadOnlyList<decimal> Amounts, bool RoundUp);

/// <summary>GC4: the insurance offer; shown after the main price, never pre-ticked.</summary>
[SparkContract]
public sealed record SparkInsuranceOffer(string Provider, Money Premium, Money InsuredAmount, string? TermsUrl, TranslatedString? Description);

/// <summary>
/// The last step (<c>GET /spark/commerce/checkout/summary</c>, GA2, CRD Art. 6 and 8(2)): the main characteristics, the
/// total FinalPrice, the sellers (their trader pages identify them, GA5), the documents in force, and the button text key.
/// </summary>
/// <param name="Sellers">The sellers of the basket (null entries are the platform's own sale).</param>
/// <param name="MinimumAge">GB12: the highest minimum age of the lines; the attestation is required when set.</param>
/// <param name="RequiresSignIn">D4: a line's channel requires sign-in.</param>
/// <param name="OrderButtonKey">The translation key of the pay button: "Order with obligation to pay" (CRD Art. 8(2)).</param>
/// <param name="SoftOptInOffered">GA9: the marketing soft opt-in checkbox may be shown (consent primitive wired).</param>
/// <param name="NeedsEmail">A guest must give an e-mail address (confirmation, durable medium).</param>
[SparkContract]
public sealed record SparkCheckoutSummary(
    IReadOnlyList<SparkCheckoutLine> Lines,
    IReadOnlyList<SparkCheckoutAmount> Extras,
    Money Total,
    IReadOnlyList<string?> Sellers,
    IReadOnlyList<SparkCheckoutDocument> Documents,
    int? MinimumAge,
    bool RequiresSignIn,
    bool SignedIn,
    IReadOnlyList<SparkCheckoutMethod> Methods,
    int? PaymentWindowMinutes,
    DateTimeOffset? EarliestHoldUntil,
    string OrderButtonKey,
    SparkDonationOffer? Donation,
    SparkInsuranceOffer? Insurance,
    bool SoftOptInOffered,
    bool NeedsEmail);

/// <summary><c>POST /spark/commerce/checkout/place</c>: press "Order with obligation to pay".</summary>
/// <param name="AcceptedDocumentIds">GA2: the ids of the documents accepted; must equal every required one in force.</param>
/// <param name="AttestedMinimumAge">GB12: the age the buyer attests to (≥ the summary's minimum age).</param>
/// <param name="MarketingOptIn">GA9: the marketing opt-in checkbox: consent (ePrivacy Art. 13(1)), stricter than the soft opt-in of Art. 13(2); recorded as a consent record.</param>
/// <param name="AdmissionTokens">§4.6, §4.12: admission tokens by scope; they win over the ones the lines were added with.</param>
/// <param name="Data">Data for the pre-payment checks (<see cref="ICheckoutPrePaymentCheck"/>), by key: Ticketing.Events reads <c>attendeeAnswers</c>.</param>
/// <param name="Language">The buyer's language, for the consent texts the checks record.</param>
[SparkContract]
public sealed record SparkPlaceOrderRequest(
    IReadOnlyList<string> AcceptedDocumentIds,
    int? AttestedMinimumAge = null,
    string? Email = null,
    string? Name = null,
    bool MarketingOptIn = false,
    string? Method = null,
    Dictionary<string, string>? AdmissionTokens = null,
    Dictionary<string, string>? Data = null,
    string? Language = null);

/// <summary>What placing answers: go to <paramref name="RedirectUrl"/> (the provider) or straight to the order page.</summary>
[SparkContract]
public sealed record SparkPlaceOrderResult(string Number, string State, string? RedirectUrl);

/// <summary>A refusal: translate <c>commerceCheckout.refusal.{code}</c>.</summary>
[SparkContract]
public sealed record SparkCheckoutRefusal(string Code, string? LineId, string? Subject);

/// <summary>An order line as the buyer sees it.</summary>
[SparkContract]
public sealed record SparkOrderLine(string LineId, TranslatedString? Title, int Quantity, IReadOnlyList<string> Units, Money Total, string Status, bool IdCheckRequired);

/// <summary>The buyer's own order (<c>GET /spark/commerce/checkout/order?number=</c>).</summary>
[SparkContract]
public sealed record SparkOrderView(
    string Number,
    string State,
    Money Total,
    Money? AmountRefunded,
    IReadOnlyList<SparkOrderLine> Lines,
    IReadOnlyList<SparkCheckoutAmount> Extras,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset? PaymentPendingUntil,
    string? PaymentUrl);

/// <summary><c>POST /spark/commerce/checkout/order/cancel-payment</c>: the buyer abandons the payment (D3: cancelled actively, holds back at once).</summary>
[SparkContract]
public sealed record SparkOrderNumberRequest(string Number);

/// <summary><c>POST /spark/commerce/checkout/basket/extend-hold</c>: "More time" (GA10). The body is empty: the action is the whole request.</summary>
public sealed record SparkBasketExtendHoldRequest;

// ---- customer account area (GB10) ---------------------------------------------------------------------------------

/// <summary>One order in the signed-in buyer's history (<c>GET /spark/commerce/checkout/account/orders</c>).</summary>
/// <param name="Titles">The first lines' titles, for the list; the order page shows them all.</param>
[SparkContract]
public sealed record SparkAccountOrder(
    string Number,
    string State,
    Money Total,
    Money? AmountRefunded,
    int LineCount,
    IReadOnlyList<TranslatedString?> Titles,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ConfirmedAt);

/// <summary>A page of the signed-in buyer's orders, newest first.</summary>
[SparkContract]
public sealed record SparkAccountOrderPage(IReadOnlyList<SparkAccountOrder> Orders, int Total, int Skip, int Take);

/// <summary>
/// D4, GB10: guest orders placed with the account's confirmed e-mail address that can move onto the account
/// (<c>GET /spark/commerce/checkout/account/guest-orders</c>). <paramref name="EmailConfirmed"/> false: the account must
/// confirm its address first, and <paramref name="Numbers"/> is empty.
/// </summary>
[SparkContract]
public sealed record SparkGuestOrdersInfo(bool EmailConfirmed, IReadOnlyList<string> Numbers);

/// <summary><c>POST /spark/commerce/checkout/account/guest-orders/attach</c>: the orders that moved onto the account.</summary>
[SparkContract]
public sealed record SparkGuestOrdersAttached(IReadOnlyList<string> Numbers);

/// <summary><c>POST /spark/commerce/checkout/account/guest-orders/attach</c>. Empty: every listed order moves.</summary>
public sealed record SparkAttachGuestOrdersRequest;

// ---- back office (GB7) --------------------------------------------------------------------------------------------

/// <summary>A line of a back-office order.</summary>
[SparkContract]
public sealed record SparkBackOfficeLine(string ItemId, int Quantity, IReadOnlyList<string>? Units = null, Dictionary<string, string>? Options = null);

/// <summary>
/// <c>POST /spark/commerce/checkout/box-office/orders</c> (GB7). <paramref name="Origin"/>: <c>phone</c> (payment link
/// mailed), <c>manual</c> (marked paid later by finance), <c>comp</c> or <c>guestList</c> (zero total).
/// </summary>
[SparkContract]
public sealed record SparkBackOfficeOrderRequest(
    string OrganizationId,
    string Origin,
    IReadOnlyList<SparkBackOfficeLine> Lines,
    string? BuyerEmail,
    string? BuyerName,
    string? GuestList = null,
    string? Note = null,
    int? AttestedMinimumAge = null);

/// <summary><c>POST /spark/commerce/checkout/box-office/mark-paid</c>: finance marks a manual order paid (audited).</summary>
[SparkContract]
public sealed record SparkMarkPaidRequest(string OrderId, string PaymentReference, string? Note = null);

/// <summary><c>POST /spark/commerce/checkout/box-office/resend</c>.</summary>
[SparkContract]
public sealed record SparkOrderIdRequest(string OrderId);

/// <summary>
/// <c>POST /spark/commerce/checkout/box-office/change-line</c>: a seat change or upgrade (GB7): new seats and/or a new
/// item, the price difference charged (payment link) or refunded.
/// </summary>
[SparkContract]
public sealed record SparkLineChangeRequest(string OrderId, string LineId, string? NewItemId = null, IReadOnlyList<string>? Units = null);

/// <summary>A back-office result: the order and, for a phone order or an upgrade, the payment link.</summary>
[SparkContract]
public sealed record SparkBackOfficeResult(string OrderId, string Number, string State, Money? Difference, string? PaymentUrl);
