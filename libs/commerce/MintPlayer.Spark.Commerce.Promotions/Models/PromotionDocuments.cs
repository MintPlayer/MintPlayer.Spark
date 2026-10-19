using System.Security.Cryptography;
using System.Text;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Commerce.Promotions.Models;

// Names carry the "Promotion" prefix: the same CLR simple name in two libraries silently shares a collection (S0).

/// <summary>What a code does (§4.6, D18). One effect per code.</summary>
public enum PromotionEffect
{
    /// <summary>A percentage off the eligible lines (<see cref="PromotionCode.PercentBasisPoints"/>), spread pro rata.</summary>
    PercentageOff,

    /// <summary>A fixed amount off the eligible lines (<see cref="PromotionCode.Amount"/>), spread pro rata, never below zero.</summary>
    FixedAmountOff,

    /// <summary>
    /// Opens hidden items or a presale phase: a capacity source gives the item an admission scope
    /// <c>promotions:{scope}</c> and the buyer presents the code as the admission token (D17). A presale access code is
    /// an unlock code of a phase's scope.
    /// </summary>
    Unlock,

    /// <summary>A quota of an item held for the code's buyers (<see cref="PromotionCode.BlockId"/>): taken out of public sale.</summary>
    CapacityBlock,
}

/// <summary>
/// A promotion code (§4.6). Codes are unique across the platform: the id is <c>PromotionCodes/{CODE}</c>, so the buyer's
/// code is a point load and a taken code cannot be created twice. Written by the promotions package only (the admin
/// endpoints, the waiting list); shown read-only through Spark to the seller's staff.
/// </summary>
public sealed class PromotionCode
{
    public string? Id { get; set; }

    /// <summary>The normalized code (upper case, A–Z, 0–9, '-' and '_').</summary>
    public string Code { get; set; } = "";

    /// <summary>The seller whose items it applies to; null for a platform code (every seller).</summary>
    public string? OrganizationId { get; set; }

    public PromotionEffect Effect { get; set; }

    /// <summary>For <see cref="PromotionEffect.PercentageOff"/>: 1500 = 15 %.</summary>
    public int? PercentBasisPoints { get; set; }

    /// <summary>For <see cref="PromotionEffect.FixedAmountOff"/>: the amount off the order (its currency must match the basket).</summary>
    public Money? Amount { get; set; }

    /// <summary>The items it applies to (discounts) or opens; empty: every item of <see cref="OrganizationId"/>.</summary>
    public List<string> ItemIds { get; set; } = [];

    /// <summary>For <see cref="PromotionEffect.Unlock"/>: the admission scopes it opens, without the <c>promotions:</c> prefix (a presale phase, a hidden set).</summary>
    public List<string> Scopes { get; set; } = [];

    /// <summary>A discount needs at least this much of eligible goods.</summary>
    public Money? MinimumOrderTotal { get; set; }

    public DateTimeOffset? ValidFrom { get; set; }

    public DateTimeOffset? ValidUntil { get; set; }

    /// <summary>How many orders (or, for an unlock code, admissions) may use it; null: unlimited.</summary>
    public int? MaxUses { get; set; }

    /// <summary>Once per buyer: needs a signed-in buyer (an account or, for an unlock code, a visitor).</summary>
    public bool OncePerBuyer { get; set; }

    /// <summary>A personal code (a waiting-list offer): only this account may use it.</summary>
    public string? AssignedToUserId { get; set; }

    /// <summary>For <see cref="PromotionEffect.CapacityBlock"/>: the block it sells from.</summary>
    public string? BlockId { get; set; }

    /// <summary>For a block code: the most units one order may take from it (a waiting-list offer's quantity).</summary>
    public int? MaxQuantityPerOrder { get; set; }

    /// <summary>The waiting-list entry this code is the offer of.</summary>
    public string? WaitingListEntryId { get; set; }

    public bool Active { get; set; } = true;

    public string? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public static string IdFor(string normalizedCode) => "PromotionCodes/" + normalizedCode;
}

/// <summary>
/// A capacity block (§4.6 "block capacity"): units of one item taken out of public sale by converted inventory claims
/// and put into a quota of its own (<see cref="QuotaId"/>), from which only the block's codes sell. Owned by a
/// capacity-blocking code or by a waiting list. Closing it returns what was not sold to public sale.
/// </summary>
public sealed class PromotionBlock
{
    public string? Id { get; set; }

    /// <summary>The code or waiting list that owns it.</summary>
    public string OwnerId { get; set; } = "";

    public string? OrganizationId { get; set; }

    /// <summary>The item whose units it holds.</summary>
    public string ItemId { get; set; } = "";

    /// <summary>The block's own inventory quota: <c>promotions/blocks/{guid}</c>.</summary>
    public string QuotaId { get; set; } = "";

    /// <summary>Units moved into the block so far (its quota's capacity).</summary>
    public int Capacity { get; set; }

    /// <summary>The converted claims that took the units out of public sale, with what came back from each.</summary>
    public List<PromotionBlockClaim> Claims { get; set; } = [];

    /// <summary>A grow or return in progress (written before the inventory call, cleared after): the reconciliation finishes it.</summary>
    public PromotionBlockStep? Pending { get; set; }

    public int Sequence { get; set; }

    /// <summary>When it stops selling; the reconciliation then returns the spare units.</summary>
    public DateTimeOffset? ClosesAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>One converted claim of a block.</summary>
public sealed class PromotionBlockClaim
{
    public string ClaimId { get; set; } = "";

    public int Quantity { get; set; }

    public int Returned { get; set; }
}

/// <summary>A block change that must finish: <c>grow</c> or <c>return</c> of <see cref="Quantity"/> units, keyed by <see cref="Key"/>.</summary>
public sealed class PromotionBlockStep
{
    public string Kind { get; set; } = "";

    public string Key { get; set; } = "";

    public int Quantity { get; set; }

    /// <summary>For a return: the block claim it returns from.</summary>
    public string? ClaimId { get; set; }

    /// <summary>The block capacity once the step is done.</summary>
    public int TargetCapacity { get; set; }
}

/// <summary>
/// A seller's sale price for one item with a validity window (GA3: a price change, not a code). The regular price is
/// snapshotted when it is scheduled. Sale starts and ends are appended to Commerce's price history; the storefront shows
/// the Omnibus prior price beside it (PID Art. 6a).
/// </summary>
public sealed class PromotionSalePrice
{
    /// <summary><c>PromotionSalePrices/{itemId}/{validFrom ticks}</c>: an item's sales load by prefix, no index.</summary>
    public string? Id { get; set; }

    public string ItemId { get; set; } = "";

    public string Channel { get; set; } = "";

    public string? OrganizationId { get; set; }

    /// <summary>The unit price during the sale, on the item's <see cref="PriceBasis"/>.</summary>
    public Money SalePrice { get; set; } = Money.Zero("EUR");

    /// <summary>The unit price when the sale was scheduled.</summary>
    public Money RegularPrice { get; set; } = Money.Zero("EUR");

    public PriceBasis PriceBasis { get; set; } = PriceBasis.Gross;

    public int? VatRateBasisPoints { get; set; }

    public DateTimeOffset ValidFrom { get; set; }

    public DateTimeOffset ValidUntil { get; set; }

    /// <summary>
    /// The start of the first step of a progressive reduction (PID Art. 6a(5)): the prior price stays the one before it.
    /// Equal to <see cref="ValidFrom"/> for a standalone sale.
    /// </summary>
    public DateTimeOffset SeriesStart { get; set; }

    /// <summary>
    /// Goods liable to deteriorate or expire rapidly (PID Art. 6a(3), a member-state option): the host exempts the item
    /// and no prior price is shown. ⚠️ Confirm per member state (D37).
    /// </summary>
    public bool PerishableExempt { get; set; }

    public bool Cancelled { get; set; }

    public DateTimeOffset? StartRecordedAt { get; set; }

    public DateTimeOffset? EndRecordedAt { get; set; }

    public string? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public static string PrefixFor(string itemId) => $"PromotionSalePrices/{itemId}/";

    public static string IdFor(string itemId, DateTimeOffset validFrom) => PrefixFor(itemId) + validFrom.UtcTicks.ToString("D19");

    public bool IsActiveAt(DateTimeOffset now) => !Cancelled && ValidFrom <= now && now < ValidUntil;
}

/// <summary>The status of a waiting list (ticket mode).</summary>
public enum PromotionWaitingListStatus
{
    /// <summary>Taking entries; freed capacity still goes to the public or the queue.</summary>
    Open,

    /// <summary>The activation rule held (sold out, queue closed, no holds): freed capacity is offered to the list.</summary>
    Active,

    /// <summary>Ended: spare units went back, open offers lapse.</summary>
    Closed,
}

/// <summary>
/// A waiting list for one item, usually a sale phase's ticket type (§4.6, D18, ticket mode). It activates only when the
/// item is sold out, its queue has closed and no holds are outstanding; from then on every freed unit moves into the
/// list's block and is offered to the next entry as a personal, time-limited code.
/// </summary>
public sealed class PromotionWaitingList
{
    public string? Id { get; set; }

    public string? OrganizationId { get; set; }

    public TranslatedString? Title { get; set; }

    /// <summary>The item offered.</summary>
    public string ItemId { get; set; } = "";

    /// <summary>The waiting-room scope whose queue must have closed (§4.12); null: the sale has no queue.</summary>
    public string? QueueScope { get; set; }

    /// <summary>Set by staff (or by an <see cref="IWaitingListQueueState"/>) when the queue closed.</summary>
    public DateTimeOffset? QueueClosedAt { get; set; }

    /// <summary>The offer validity; null takes <see cref="PromotionWaitingListOptions.OfferValidity"/>.</summary>
    public TimeSpan? OfferValidity { get; set; }

    public PromotionWaitingListStatus Status { get; set; }

    public DateTimeOffset? ActivatedAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>The list's block (created at activation).</summary>
    public string? BlockId { get; set; }

    /// <summary>Entries made so far: an entry's position.</summary>
    public int EntrySequence { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public enum PromotionWaitingListEntryStatus { Waiting, Offered, Purchased, OfferExpired, Left }

/// <summary>One buyer on a waiting list. Ticket mode needs an account. Erased with the account (GDPR).</summary>
public sealed class PromotionWaitingListEntry
{
    /// <summary><c>PromotionWaitingListEntries/{list guid}/{hash of the user id}</c>: one entry per buyer and list.</summary>
    public string? Id { get; set; }

    public string ListId { get; set; } = "";

    public string UserId { get; set; } = "";

    public int Quantity { get; set; } = 1;

    public int Position { get; set; }

    public PromotionWaitingListEntryStatus Status { get; set; }

    public DateTimeOffset JoinedAt { get; set; }

    public string? OfferCode { get; set; }

    public DateTimeOffset? OfferedAt { get; set; }

    public DateTimeOffset? OfferExpiresAt { get; set; }

    public static string IdFor(string listId, string userId)
        => $"PromotionWaitingListEntries/{listId.Split('/')[^1]}/{PromotionHashes.Short(userId)}";
}

/// <summary>
/// A back-in-stock subscription (GB11): a buyer (account, or an e-mail address confirmed by double opt-in) asks to be
/// mailed when an item is available again. No code and no reservation: the first payer wins. Lapses after
/// <see cref="BackInStockOptions.SubscriptionLifetime"/>; erased with the account.
/// </summary>
public sealed class PromotionStockSubscription
{
    /// <summary><c>PromotionStockSubscriptions/{itemId}/{hash of the address}</c>: one per item and address.</summary>
    public string? Id { get; set; }

    public string ItemId { get; set; } = "";

    public string? OrganizationId { get; set; }

    public string? UserId { get; set; }

    /// <summary>The address, lower case.</summary>
    public string Email { get; set; } = "";

    public string? Culture { get; set; }

    /// <summary>Confirmed: an account's verified address, or a guest who followed the confirmation link.</summary>
    public bool Confirmed { get; set; }

    /// <summary>SHA-256 of the confirmation/unsubscribe token (the token itself travels only in the mail).</summary>
    public string TokenHash { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ConfirmedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? NotifiedAt { get; set; }

    public static string IdFor(string itemId, string email) => $"PromotionStockSubscriptions/{itemId}/{PromotionHashes.Short(email.Trim().ToLowerInvariant())}";
}

/// <summary>What the back-in-stock job saw of an item last time (GB11): a rise from zero is a restock.</summary>
public sealed class PromotionStockWatch
{
    public string? Id { get; set; }

    public string ItemId { get; set; } = "";

    public int LastAvailable { get; set; }

    public DateTimeOffset? LastRestockAt { get; set; }

    public static string IdFor(string itemId) => "PromotionStockWatches/" + itemId;
}

/// <summary>
/// A promoter or affiliate tracking link (GC2): <c>?ref={Ref}</c> plus UTM parameters. Sales attributed to it are
/// reported per link; an optional commission is booked on confirmed orders.
/// </summary>
public sealed class PromotionTrackingLink
{
    /// <summary><c>PromotionTrackingLinks/{REF}</c>.</summary>
    public string? Id { get; set; }

    public string Ref { get; set; } = "";

    /// <summary>The seller whose sales it attributes; null for a platform link (every seller).</summary>
    public string? OrganizationId { get; set; }

    public string Name { get; set; } = "";

    /// <summary>The promoter's account, when they have one (they may see their own report).</summary>
    public string? PromoterUserId { get; set; }

    public string? UtmSource { get; set; }

    public string? UtmMedium { get; set; }

    public string? UtmCampaign { get; set; }

    public string? UtmContent { get; set; }

    /// <summary>Commission on the attributed goods (1000 = 10 %).</summary>
    public int? CommissionBasisPoints { get; set; }

    /// <summary>A fixed commission per confirmed order.</summary>
    public Money? CommissionFixed { get; set; }

    public bool Active { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public static string IdFor(string normalizedRef) => "PromotionTrackingLinks/" + normalizedRef;
}

/// <summary>
/// A visitor's or account's attribution (GC2), first touch within the window. Kept per identity, like the basket, and
/// moved onto the account at sign-in (X1). Written only in cluster-wide sessions (the visitor merge is one).
/// </summary>
public sealed class PromotionAttribution
{
    /// <summary><c>PromotionAttributions/{user|visitor}/{hash}</c>.</summary>
    public string? Id { get; set; }

    public string Ref { get; set; } = "";

    public string? UtmSource { get; set; }

    public string? UtmMedium { get; set; }

    public string? UtmCampaign { get; set; }

    public string? UtmContent { get; set; }

    public DateTimeOffset AttributedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public static string IdForUser(string userId) => "PromotionAttributions/user/" + PromotionHashes.Short(userId);

    public static string IdForVisitor(string visitorId) => "PromotionAttributions/visitor/" + PromotionHashes.Short(visitorId);
}

public enum PromotionOrderAttributionStatus { Placed, Confirmed, Cancelled }

/// <summary>An order's attribution to a tracking link (GC2): the satellite of the order, so Checkout's order stays untouched.</summary>
public sealed class PromotionOrderAttribution
{
    /// <summary><c>PromotionOrderAttributions/{orderId}</c>.</summary>
    public string? Id { get; set; }

    public string OrderId { get; set; } = "";

    public string OrderNumber { get; set; } = "";

    public string Ref { get; set; } = "";

    public string? OrganizationId { get; set; }

    public string? UtmSource { get; set; }

    public string? UtmMedium { get; set; }

    public string? UtmCampaign { get; set; }

    public string? UtmContent { get; set; }

    /// <summary>The attributed goods: the lines of the link's seller (all lines for a platform link).</summary>
    public Money AttributedTotal { get; set; } = Money.Zero("EUR");

    public PromotionOrderAttributionStatus Status { get; set; }

    public DateTimeOffset AttributedAt { get; set; }

    public DateTimeOffset PlacedAt { get; set; }

    public DateTimeOffset? ConfirmedAt { get; set; }

    public static string IdFor(string orderId) => "PromotionOrderAttributions/" + orderId;
}

/// <summary>Hashes for natural ids that must not carry personal data in clear (an address, a user id).</summary>
public static class PromotionHashes
{
    /// <summary>The first 32 hex characters of SHA-256 (128 bits).</summary>
    public static string Short(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32];

    /// <summary>The full SHA-256 of a token, for comparison.</summary>
    public static string Token(string token)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>A random URL-safe token (256 bits).</summary>
    public static string NewToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Constant-time comparison of a presented token with the stored hash.</summary>
    public static bool Matches(string? token, string storedHash)
        => token is { Length: > 0 } && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Token(token)), Encoding.ASCII.GetBytes(storedHash));
}
