using System.Text.RegularExpressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Promotions.Models;

namespace MintPlayer.Spark.Commerce.Promotions;

/// <summary>Why promotions refused something. Codes are permanent: the client translates <c>commercePromotions.refusal.{code}</c>.</summary>
public static class PromotionRefusals
{
    public const string CodeInvalid = "promotionCodeInvalid";
    public const string CodeNotApplicable = "promotionCodeNotApplicable";
    public const string CodeExhausted = "promotionCodeExhausted";
    public const string CodeAlreadyUsed = "promotionCodeAlreadyUsed";
    public const string SignInRequired = "promotionSignInRequired";
    public const string MinimumNotReached = "promotionMinimumNotReached";
    public const string QuantityExceeded = "promotionQuantityExceeded";
    public const string NotYours = "promotionCodeNotYours";
    public const string CodeTaken = "promotionCodeTaken";
    public const string InvalidRequest = "promotionInvalidRequest";
    public const string NotAReduction = "promotionNotAReduction";
    public const string SaleOverlaps = "promotionSaleOverlaps";
    public const string ItemNotFound = "promotionItemNotFound";
    public const string NumberedUnits = "promotionNumberedUnits";
    public const string SoldOut = "promotionSoldOut";
    public const string ListClosed = "promotionListClosed";
}

/// <summary>A promotions refusal (<see cref="PromotionRefusals"/>).</summary>
public sealed class PromotionException(string code, string? subject = null)
    : Exception($"Promotions refused: {code}{(subject is null ? "" : " (" + subject + ")")}.")
{
    public string Code { get; } = code;

    public string? Subject { get; } = subject;
}

/// <summary>The buyer's choices the promotions steps read from the basket (<c>ISparkCheckout.SetChoicesAsync</c>).</summary>
public static class PromotionChoices
{
    /// <summary>The code the buyer entered.</summary>
    public const string Code = "promotionCode";

    /// <summary>The tracking link the basket was attributed to (informational; the order's attribution is kept by promotions).</summary>
    public const string Ref = "promotionRef";
}

/// <summary>
/// The item ids and admission scopes promotions owns. A capacity-blocking code sells the wrapped item
/// <c>promotions:{CODE}|{itemId}</c> from its block; an unlock code opens the admission scope <c>promotions:{scope}</c>.
/// </summary>
public static class PromotionItemIds
{
    public const string Prefix = "promotions:";

    /// <summary>The item a buyer adds to buy <paramref name="itemId"/> with block code <paramref name="code"/>.</summary>
    public static string Blocked(string code, string itemId) => $"{Prefix}{code}|{itemId}";

    /// <summary>Splits a wrapped item id; false for any other id.</summary>
    public static bool TryParse(string itemId, out string code, out string innerItemId)
    {
        code = innerItemId = "";
        if (!itemId.StartsWith(Prefix, StringComparison.Ordinal))
            return false;
        var bar = itemId.IndexOf('|', Prefix.Length);
        if (bar <= Prefix.Length || bar == itemId.Length - 1)
            return false;
        code = itemId[Prefix.Length..bar];
        innerItemId = itemId[(bar + 1)..];
        return true;
    }

    /// <summary>The item a line is about: the inner item of a wrapped id, else the id itself.</summary>
    public static string Inner(string itemId) => TryParse(itemId, out _, out var inner) ? inner : itemId;

    /// <summary>The admission scope of an unlock code's scope (what a capacity source puts on a hidden item or presale phase).</summary>
    public static string Scope(string scope) => Prefix + scope;
}

/// <summary>
/// Promotions' pure rules: code normalization and validity, the discount and its pro-rata spread, the Omnibus reference
/// and reduction, the waiting list's activation rule, the back-in-stock batch, attribution and commission. No I/O, no
/// clock: callers pass the time.
/// </summary>
public static partial class PromotionRules
{
    [GeneratedRegex("^[A-Z0-9_-]{3,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    /// <summary>The stored form of a code the buyer typed (case and surrounding spaces ignored), or null when it can never be valid.</summary>
    public static string? NormalizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;
        var normalized = code.Trim().ToUpperInvariant();
        return CodePattern().IsMatch(normalized) ? normalized : null;
    }

    /// <summary>Whether <paramref name="code"/> can be used at <paramref name="now"/>: active and inside its window.</summary>
    public static bool IsLive(PromotionCode code, DateTimeOffset now)
        => code.Active && (code.ValidFrom is null || code.ValidFrom <= now) && (code.ValidUntil is null || now < code.ValidUntil);

    /// <summary>Whether a line of <paramref name="itemId"/> sold by <paramref name="sellerId"/> is eligible for <paramref name="code"/>.</summary>
    public static bool Applies(PromotionCode code, string itemId, string? sellerId)
    {
        if (code.OrganizationId is not null && !string.Equals(code.OrganizationId, sellerId, StringComparison.Ordinal))
            return false;
        return code.ItemIds.Count == 0 || code.ItemIds.Contains(PromotionItemIds.Inner(itemId), StringComparer.Ordinal);
    }

    /// <summary>
    /// The total discount of <paramref name="code"/> over goods worth <paramref name="eligible"/> (never more than them,
    /// so a line never goes negative). Percentages are rounded once (<see cref="Money.Share"/>, half away from zero, S21).
    /// </summary>
    public static Money Discount(PromotionCode code, Money eligible)
    {
        if (eligible.ToMinorUnits() <= 0)
            return Money.Zero(eligible.Currency);
        var discount = code.Effect switch
        {
            PromotionEffect.PercentageOff when code.PercentBasisPoints is { } bp => eligible.Share(Math.Clamp(bp, 0, 10_000), 10_000),
            PromotionEffect.FixedAmountOff when code.Amount is { } amount && amount.Currency == eligible.Currency => amount,
            _ => Money.Zero(eligible.Currency),
        };
        return discount > eligible ? eligible : discount;
    }

    /// <summary>
    /// Spreads an order discount over the eligible lines pro rata to their amounts (§4.6, D19): the parts sum exactly to
    /// the discount, each within one cent of its share (largest remainder, X4/S21: <see cref="Money.Allocate(IReadOnlyList{Money})"/>).
    /// This split is what a partial refund gives back and what each line's VAT base is reduced by. Returns negative
    /// amounts (adjustments) in line order.
    /// </summary>
    public static Money[] SpreadProRata(Money discount, IReadOnlyList<Money> lineAmounts)
    {
        if (lineAmounts.Count == 0)
            return [];
        if (discount.IsZero || lineAmounts.All(a => a.ToMinorUnits() <= 0))
            return lineAmounts.Select(a => Money.Zero(discount.Currency)).ToArray();
        var weights = lineAmounts.Select(a => Math.Max(0, a.ToMinorUnits())).ToArray();
        var parts = (-discount).Allocate(weights);
        return parts;
    }

    /// <summary>
    /// A line adjustment split per unit (largest remainder): what a refund of some of the line's units gives back of the
    /// discount. The parts sum exactly to the adjustment.
    /// </summary>
    public static Money[] PerUnit(Money adjustment, int quantity)
        => quantity <= 0 ? [] : adjustment.Allocate(Enumerable.Repeat(1L, quantity).ToArray());

    /// <summary>
    /// The start of the reduction a new sale belongs to (PID Art. 6a(5)): a sale that follows a previous one without a gap
    /// and goes lower again continues its series, so the prior price stays the one before the first step. Otherwise it
    /// starts a series of its own.
    /// </summary>
    public static DateTimeOffset SeriesStart(PromotionSalePrice? previous, DateTimeOffset validFrom, Money salePrice)
        => previous is { Cancelled: false } p && p.ValidUntil >= validFrom && p.ValidFrom < validFrom && salePrice <= p.SalePrice
            ? p.SeriesStart
            : validFrom;

    /// <summary>
    /// The reduction announced against the prior price, in whole percent rounded down (so it never overstates): the
    /// Commission's guidance on Art. 6a reads a percentage as computed on the prior price. Null without a prior price or
    /// when the sale is not lower than it.
    /// </summary>
    public static int? ReductionPercent(Money? priorFinal, Money saleFinal)
    {
        if (priorFinal is null || priorFinal.Currency != saleFinal.Currency || priorFinal.ToMinorUnits() <= 0 || saleFinal >= priorFinal)
            return null;
        var saved = priorFinal.ToMinorUnits() - saleFinal.ToMinorUnits();
        return (int)(saved * 100 / priorFinal.ToMinorUnits());
    }

    /// <summary>
    /// The waiting list's activation rule (§4.6, D18, ticket mode): the item is sold out, its queue has closed, and no
    /// holds are outstanding. Until then freed capacity goes to the queue (or the public).
    /// </summary>
    public static bool ShouldActivate(int available, int held, bool queueClosed) => available <= 0 && held <= 0 && queueClosed;

    /// <summary>How many subscribers a restock of <paramref name="restocked"/> units mails (GB11): <c>ceil(N × factor)</c>, capped.</summary>
    public static int BackInStockBatch(int restocked, decimal factor, int max)
    {
        if (restocked <= 0 || factor <= 0)
            return 0;
        var batch = (int)Math.Min(int.MaxValue, Math.Ceiling(restocked * factor));
        return Math.Clamp(batch, 1, Math.Max(1, max));
    }

    /// <summary>
    /// Whether a new attribution replaces <paramref name="existing"/> (GC2): first touch within the window, so only an
    /// expired one is replaced.
    /// </summary>
    public static bool ReplacesAttribution(PromotionAttribution? existing, DateTimeOffset now) => existing is null || existing.ExpiresAt <= now;

    /// <summary>A tracking link's commission on <paramref name="attributed"/> goods: percentage (rounded once) plus the fixed amount.</summary>
    public static Money Commission(PromotionTrackingLink link, Money attributed)
    {
        var commission = Money.Zero(attributed.Currency);
        if (link.CommissionBasisPoints is { } bp && attributed.ToMinorUnits() > 0)
            commission += attributed.Share(Math.Clamp(bp, 0, 10_000), 10_000);
        if (link.CommissionFixed is { } fixedAmount && fixedAmount.Currency == attributed.Currency)
            commission += fixedAmount;
        return commission;
    }
}
