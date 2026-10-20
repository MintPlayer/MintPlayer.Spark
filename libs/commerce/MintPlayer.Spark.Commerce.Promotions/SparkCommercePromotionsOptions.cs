using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.Consent;

namespace MintPlayer.Spark.Commerce.Promotions;

/// <summary>
/// <c>Spark:Commerce:Promotions</c> (commerce PRD §4.6, D18; round 5: GA3, GB11, GC2). Code given to
/// <c>AddCommercePromotions(o => …)</c> runs first; the section is bound after it (configuration wins, §4.2).
/// </summary>
public sealed class SparkCommercePromotionsOptions : ISparkConfigurationSection
{
    public static string SectionName => "Spark:Commerce:Promotions";

    /// <summary>The organization custom role (X8) that manages a seller's codes, sale prices, waiting lists and tracking links, besides its Owners and Admins. Default <c>promotions</c>.</summary>
    public string ManagerRole { get; set; } = "promotions";

    /// <summary>
    /// How long a usage reservation made at "Pay" lives before the order-event recipient extends it to the order's
    /// payment deadline (a crash between the two leaves a reservation that lapses on its own). Default 30 minutes.
    /// </summary>
    public TimeSpan UsageReservation { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Whether a full refund gives the code's use back (default false: a use is spent once the order was confirmed).</summary>
    public bool ReleaseUseOnRefund { get; set; }

    /// <summary>Concurrency retries on a code's usage counter. Default 8.</summary>
    public int MaxAttempts { get; set; } = 8;

    public PromotionWaitingListOptions WaitingList { get; set; } = new();

    public BackInStockOptions BackInStock { get; set; } = new();

    public SalePriceOptions SalePrices { get; set; } = new();

    public TrackingOptions Tracking { get; set; } = new();

    /// <summary>The block reconciliation, the waiting-list evaluation and the offer expiry (six fields = seconds). Default every 30 seconds.</summary>
    public string BlocksSchedule { get; set; } = "*/30 * * * * *";
}

/// <summary>The ticket mode of the waiting list (§4.6, D18).</summary>
public sealed class PromotionWaitingListOptions
{
    /// <summary>How long a personal offer stays valid (e.g. 24 h). A list may set its own.</summary>
    public TimeSpan OfferValidity { get; set; } = TimeSpan.FromHours(24);

    /// <summary>The most tickets one entry may ask for. Default 4.</summary>
    public int MaxQuantityPerEntry { get; set; } = 4;

    /// <summary>The storefront path of an offer, with <c>{code}</c> (the mail links it). Default <c>/promotions/offer?code={code}</c>.</summary>
    public string OfferPagePath { get; set; } = "/promotions/offer?code={code}";

    public string OfferMailTemplate { get; set; } = "SparkPromotions/WaitingListOffer";
}

/// <summary>Back-in-stock notifications, the shop mode of the waiting list (GB11).</summary>
public sealed class BackInStockOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>A restock of N units mails <c>ceil(N × factor)</c> subscribers, oldest first. Default 2.</summary>
    public decimal BatchFactor { get; set; } = 2m;

    /// <summary>The most subscribers one restock mails, whatever the quantity. Default 500.</summary>
    public int MaxBatch { get; set; } = 500;

    /// <summary>A subscription lapses this long after it was made. Default 90 days.</summary>
    public TimeSpan SubscriptionLifetime { get; set; } = TimeSpan.FromDays(90);

    /// <summary>A guest's subscription that was never confirmed (double opt-in) is deleted after this. Default 48 hours.</summary>
    public TimeSpan ConfirmationWindow { get; set; } = TimeSpan.FromHours(48);

    /// <summary>
    /// The notification is a service mail the buyer asked for, not marketing, so it needs no consent (GB11; ePrivacy
    /// Art. 13 covers unsolicited marketing). A host whose counsel reads it otherwise turns this on: the mail is then
    /// sent only with <see cref="SparkConsentPurposes.MarketingMail"/> granted (X21), and startup refuses without the
    /// consent primitive.
    /// </summary>
    public bool RequireMarketingConsent { get; set; }

    /// <summary>The evaluation and expiry job (six fields = seconds). Default every minute.</summary>
    public string Schedule { get; set; } = "0 * * * * *";

    /// <summary>The storefront path of the item, with <c>{itemId}</c>. Default <c>/shop/item?id={itemId}</c>.</summary>
    public string ItemPagePath { get; set; } = "/shop/item?id={itemId}";

    /// <summary>The page that confirms a guest's subscription, with <c>{id}</c> and <c>{token}</c>.</summary>
    public string ConfirmPagePath { get; set; } = "/promotions/back-in-stock/confirm?id={id}&token={token}";

    /// <summary>The page that ends a subscription from the mail, with <c>{id}</c> and <c>{token}</c>.</summary>
    public string UnsubscribePagePath { get; set; } = "/promotions/back-in-stock/unsubscribe?id={id}&token={token}";

    public string NotificationMailTemplate { get; set; } = "SparkPromotions/BackInStock";

    public string ConfirmationMailTemplate { get; set; } = "SparkPromotions/BackInStockConfirm";
}

/// <summary>Seller sale prices and the Omnibus prior price (GA3).</summary>
public sealed class SalePriceOptions
{
    /// <summary>
    /// Whether the pricing step applies an active sale price to basket lines as an adjustment (default true). Turn it
    /// off when every capacity source already puts the sale price into <c>CheckoutItem.Price</c> through
    /// <see cref="ISparkSalePrices.ApplyAsync"/>.
    /// </summary>
    public bool ApplyInPricingStep { get; set; } = true;

    /// <summary>
    /// The member states (ISO 3166-1 alpha-2) that adopted the progressive-reduction option of PID Art. 6a(5) ("Member
    /// States may provide …"): there a sale that follows the previous one without a gap and goes lower again keeps the
    /// prior price from before the first step. Keyed by the seller's country (its trader data; the platform's
    /// <c>Spark:Commerce:Country</c> for its own items). Empty by default: every step is a reduction of its own with its
    /// own 30-day prior price (Art. 6a(1)–(2)). ⚠️ List a state only after checking its transposition (D37).
    /// </summary>
    public List<string> ProgressiveReductionStates { get; set; } = [];

    /// <summary>Recording sale starts and ends in the price history (six fields = seconds). Default every minute.</summary>
    public string Schedule { get; set; } = "0 * * * * *";
}

/// <summary>Promoter and affiliate tracking links (GC2).</summary>
public sealed class TrackingOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>A visitor's first attribution counts for this long (first touch). Default 30 days.</summary>
    public TimeSpan AttributionWindow { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Whether an attribution is stored only with the visitor's consent to <see cref="ConsentPurpose"/> (GA9, X21).
    /// Default true; startup then refuses without the consent primitive.
    /// </summary>
    public bool RequireConsent { get; set; } = true;

    /// <summary>The consent purpose. Default <see cref="SparkConsentPurposes.Analytics"/>.</summary>
    public string ConsentPurpose { get; set; } = SparkConsentPurposes.Analytics;
}
