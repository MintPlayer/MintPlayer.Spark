using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Commerce.Promotions.Contracts;

// The TypeScript contracts of @mintplayer/ng-spark/commerce/promotions (X20 item 2, docs/guide-typescript-contracts.md).
// Ids travel in bodies and query strings, never in paths (RavenDB ids contain slashes).

/// <summary>A refusal of a promotions endpoint (409): a <c>PromotionRefusals</c> code, translated as <c>commercePromotions.refusal.{code}</c>.</summary>
[SparkContract]
public sealed record SparkPromotionRefusal(string Code, string? Subject);

/// <summary>What the storefront may know of a code the buyer typed (<c>GET /spark/commerce/promotions/code</c>).</summary>
/// <param name="Effect">PercentageOff, FixedAmountOff, Unlock or CapacityBlock.</param>
/// <param name="BlockedItemIds">For a block code: the wrapped item ids the basket adds (<c>promotions:{CODE}|{itemId}</c>).</param>
/// <param name="MaxQuantityPerOrder">For a waiting-list offer: how many it buys.</param>
[SparkContract]
public sealed record SparkPromotionCodeInfo(
    string Code,
    string Effect,
    int? PercentBasisPoints,
    Money? Amount,
    IReadOnlyList<string> ItemIds,
    IReadOnlyList<string> BlockedItemIds,
    int? MaxQuantityPerOrder,
    DateTimeOffset? ValidUntil);

/// <summary>A code to create or update (<c>POST /spark/commerce/promotions/admin/codes/save</c>).</summary>
/// <param name="OrganizationId">The seller; null for a platform code (platform admins only).</param>
/// <param name="Effect">PercentageOff, FixedAmountOff, Unlock or CapacityBlock.</param>
/// <param name="BlockQuantity">For CapacityBlock: the units held for the code.</param>
[SparkContract]
public sealed record SparkPromotionCodeRequest(
    string Code,
    string? OrganizationId,
    string Effect,
    int? PercentBasisPoints = null,
    Money? Amount = null,
    IReadOnlyList<string>? ItemIds = null,
    IReadOnlyList<string>? Scopes = null,
    Money? MinimumOrderTotal = null,
    DateTimeOffset? ValidFrom = null,
    DateTimeOffset? ValidUntil = null,
    int? MaxUses = null,
    bool OncePerBuyer = false,
    int? MaxQuantityPerOrder = null,
    int? BlockQuantity = null,
    bool Active = true);

/// <summary>A code as the seller's back office sees it.</summary>
[SparkContract]
public sealed record SparkPromotionCodeView(
    string Code,
    string? OrganizationId,
    string Effect,
    bool Active,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidUntil,
    int? MaxUses,
    int Uses,
    int? Blocked,
    int? BlockAvailable);

/// <summary>A code by its text (<c>POST …/admin/codes/deactivate</c>).</summary>
[SparkContract]
public sealed record SparkPromotionCodeKey(string Code);

/// <summary>What a listing shows for a price during a sale (GA3, PID Art. 6a; <c>GET /spark/commerce/promotions/sale-price</c>).</summary>
/// <param name="FinalPrice">The final price of one unit now (the sale price when on sale), every mandatory fee included (§4.4).</param>
/// <param name="AnnounceReduction">Whether a reduction may be shown (strike-through, badge, percentage): only with a prior price, or for an exempt item.</param>
/// <param name="RegularFinalPrice">The final price without the sale (the "was" price), when the reduction is announced.</param>
/// <param name="PriorPrice">The lowest final price of the 30 days before the reduction started (Art. 6a(2)); shown whenever the reduction is announced.</param>
/// <param name="PriorPriceExempt">A perishable item the host exempted (Art. 6a(3)): no prior price.</param>
/// <param name="ReductionPercent">The reduction against the prior price, rounded down; null when it cannot be announced.</param>
/// <param name="ReductionSince">The start of the reduction (of its first step, for a progressive one).</param>
[SparkContract]
public sealed record SparkSalePriceDisplay(
    string ItemId,
    Money FinalPrice,
    bool OnSale,
    bool AnnounceReduction,
    DateTimeOffset? SaleEndsAt,
    Money? RegularFinalPrice,
    Money? PriorPrice,
    bool PriorPriceExempt,
    int? ReductionPercent,
    DateTimeOffset? ReductionSince);

/// <summary>A sale to schedule (<c>POST …/admin/sale-prices/schedule</c>).</summary>
[SparkContract]
public sealed record SparkSalePriceRequest(string ItemId, Money SalePrice, DateTimeOffset ValidFrom, DateTimeOffset ValidUntil, bool PerishableExempt = false);

/// <summary>A scheduled sale.</summary>
[SparkContract]
public sealed record SparkSalePriceView(string Id, string ItemId, Money SalePrice, Money RegularPrice, DateTimeOffset ValidFrom, DateTimeOffset ValidUntil, DateTimeOffset SeriesStart, bool PerishableExempt, bool Cancelled);

/// <summary>A document by id (sale prices, lists, subscriptions).</summary>
[SparkContract]
public sealed record SparkPromotionIdRequest(string Id);

/// <summary>A waiting list to create (<c>POST …/admin/waiting-lists/create</c>).</summary>
/// <param name="QueueScope">The waiting-room scope whose queue must close first; null: the sale has no queue.</param>
[SparkContract]
public sealed record SparkWaitingListRequest(string ItemId, string? OrganizationId, TranslatedString? Title, string? QueueScope, int? OfferValidityMinutes);

/// <summary>A waiting list as the storefront and the back office see it.</summary>
/// <param name="Status">Open, Active or Closed.</param>
[SparkContract]
public sealed record SparkWaitingListView(string Id, string ItemId, TranslatedString? Title, string Status, DateTimeOffset? ActivatedAt);

/// <summary>Join a list with a quantity (<c>POST …/waiting-lists/join</c>).</summary>
[SparkContract]
public sealed record SparkWaitingListJoinRequest(string ListId, int Quantity);

/// <summary>The signed-in buyer's entry on a list.</summary>
/// <param name="Status">Waiting, Offered, Purchased, OfferExpired or Left.</param>
/// <param name="OfferCode">The personal code while an offer is open.</param>
[SparkContract]
public sealed record SparkWaitingListEntryView(string ListId, string Status, int Quantity, int Position, string? OfferCode, DateTimeOffset? OfferExpiresAt);

/// <summary>Subscribe to an item's restock (<c>POST …/back-in-stock/subscribe</c>). A guest gives an address and confirms it by mail.</summary>
[SparkContract]
public sealed record SparkBackInStockRequest(string ItemId, string? Email);

/// <summary>The answer to a subscription: whether the guest must confirm it by mail.</summary>
[SparkContract]
public sealed record SparkBackInStockResult(string Id, bool Confirmed, bool ConfirmationSent);

/// <summary>A token from a back-in-stock mail (<c>…/back-in-stock/confirm</c>, <c>…/unsubscribe</c>).</summary>
[SparkContract]
public sealed record SparkBackInStockTokenRequest(string Id, string Token);

/// <summary>One of the signed-in buyer's subscriptions (the account area, GB10).</summary>
/// <param name="Title">The item's name as checkout sells it (null when it is no longer on sale).</param>
[SparkContract]
public sealed record SparkBackInStockView(string Id, string ItemId, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, bool Notified, TranslatedString? Title);

/// <summary>A visit through a tracking link (<c>POST …/track</c>).</summary>
[SparkContract]
public sealed record SparkTrackRequest(string Ref, string? UtmSource = null, string? UtmMedium = null, string? UtmCampaign = null, string? UtmContent = null);

/// <summary>Whether the visit was attributed (false without consent, for an unknown link, or when an earlier attribution still counts).</summary>
[SparkContract]
public sealed record SparkTrackResult(bool Attributed);

/// <summary>A tracking link to create or update (<c>POST …/admin/links/save</c>).</summary>
[SparkContract]
public sealed record SparkTrackingLinkRequest(
    string Ref,
    string? OrganizationId,
    string Name,
    string? PromoterUserId = null,
    string? UtmSource = null,
    string? UtmMedium = null,
    string? UtmCampaign = null,
    string? UtmContent = null,
    int? CommissionBasisPoints = null,
    Money? CommissionFixed = null,
    bool Active = true);

/// <summary>Orders per UTM source.</summary>
[SparkContract]
public sealed record SparkTrackingSourceCount(string Source, int Orders);

/// <summary>A link's report (GC2): orders placed, confirmed and cancelled, attributed sales and commissions per currency.</summary>
[SparkContract]
public sealed record SparkTrackingReport(
    string Ref,
    int OrdersPlaced,
    int OrdersConfirmed,
    int OrdersCancelled,
    IReadOnlyList<Money> Sales,
    IReadOnlyList<Money> Commissions,
    IReadOnlyList<SparkTrackingSourceCount> BySource);
