using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Pricing;
using MintPlayer.Spark.Commerce.Promotions.Contracts;
using MintPlayer.Spark.Commerce.Promotions.Models;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Promotions;

/// <summary>A sale to schedule (GA3): <paramref name="SalePrice"/> per unit, on the item's price basis, from <paramref name="ValidFrom"/> until <paramref name="ValidUntil"/>.</summary>
/// <param name="PerishableExempt">PID Art. 6a(3): the host exempts goods liable to deteriorate or expire rapidly; no prior price is shown.</param>
public sealed record SalePriceRequest(string ItemId, Money SalePrice, DateTimeOffset ValidFrom, DateTimeOffset ValidUntil, bool PerishableExempt = false);

/// <summary>
/// Seller sale prices with a validity window and the Omnibus prior price (GA3; Price Indication Directive 98/6/EC
/// Art. 6a, inserted by Directive (EU) 2019/2161 Art. 2). A sale is a price change, not a code: its start and end are
/// appended to Commerce's price history (<see cref="ISparkPriceHistory"/>), and whenever a storefront announces the
/// reduction it shows the lowest final price of the 30 days before it (Art. 6a(1)–(2)); a progressive reduction keeps
/// the reference of its first step (Art. 6a(5)); perishable goods may be exempted per item (Art. 6a(3), a member-state
/// option). ⚠️ Confirm Art. 6a's wording and the Commission guidance (2021/C 526/02) against EUR-Lex, and the
/// member-state options per country (D37): this agent could not fetch EUR-Lex.
/// </summary>
public interface ISparkSalePrices
{
    /// <summary>Schedules a sale. The item is resolved through checkout's capacity sources (it must be on sale).</summary>
    /// <exception cref="PromotionException">Not a reduction, an overlapping sale, an unknown item, a bad window.</exception>
    Task<PromotionSalePrice> ScheduleAsync(SalePriceRequest request, string? actorId, CancellationToken cancellationToken = default);

    /// <summary>Ends a sale now (or cancels one that has not started).</summary>
    Task<PromotionSalePrice?> CancelAsync(string salePriceId, CancellationToken cancellationToken = default);

    /// <summary>The sale of <paramref name="itemId"/> in force at <paramref name="at"/>, if any.</summary>
    Task<PromotionSalePrice?> GetActiveAsync(string itemId, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>
    /// <paramref name="item"/> with its active sale price as <c>Price</c>, for a capacity source that prices sales itself
    /// (then turn <see cref="SalePriceOptions.ApplyInPricingStep"/> off). Unchanged without an active sale.
    /// </summary>
    Task<CheckoutItem> ApplyAsync(CheckoutItem item, CancellationToken cancellationToken = default);

    /// <summary>
    /// What a listing shows for <paramref name="item"/> (which the caller loaded and may show): the final price, and during
    /// a sale the reduction with its prior price. A reduction is never shown without the prior price unless the item is
    /// exempt; the percentage is computed on the prior price.
    /// </summary>
    Task<SparkSalePriceDisplay> DisplayAsync(ISellableItem item, CancellationToken cancellationToken = default);
}

/// <summary>An <see cref="ISellableItem"/> at a given price: what the price history records for a sale step.</summary>
internal sealed record PricedSellable(string? Id, string Channel, string? OrganizationId, Money? Price, PriceBasis PriceBasis, int? VatRateBasisPoints) : ISellableItem;

internal sealed class SalePrices(
    IDocumentStore store,
    Services.PromotionItems items,
    IFinalPriceCalculator calculator,
    ICommercePriceDisplay display,
    TimeProvider clock) : ISparkSalePrices
{
    public async Task<PromotionSalePrice> ScheduleAsync(SalePriceRequest request, string? actorId, CancellationToken cancellationToken = default)
    {
        if (request.ValidUntil <= request.ValidFrom || request.ValidUntil <= clock.GetUtcNow())
            throw new PromotionException(PromotionRefusals.InvalidRequest, "window");
        var item = await items.ResolveAsync(request.ItemId, 1, cancellationToken) ?? throw new PromotionException(PromotionRefusals.ItemNotFound, request.ItemId);
        if (request.SalePrice.Currency != item.Price.Currency || request.SalePrice.ToMinorUnits() < 0)
            throw new PromotionException(PromotionRefusals.InvalidRequest, "currency");
        if (request.SalePrice >= item.Price)
            throw new PromotionException(PromotionRefusals.NotAReduction, request.ItemId);

        using var session = store.OpenAsyncSession();
        var existing = await LoadAllAsync(session, request.ItemId, cancellationToken);
        if (existing.Any(s => !s.Cancelled && s.ValidFrom < request.ValidUntil && request.ValidFrom < s.ValidUntil))
            throw new PromotionException(PromotionRefusals.SaleOverlaps, request.ItemId);
        var previous = existing.Where(s => !s.Cancelled && s.ValidUntil <= request.ValidFrom).MaxBy(s => s.ValidUntil);

        var sale = new PromotionSalePrice
        {
            Id = PromotionSalePrice.IdFor(request.ItemId, request.ValidFrom),
            ItemId = request.ItemId,
            Channel = item.Channel,
            OrganizationId = item.OrganizationId,
            SalePrice = request.SalePrice,
            RegularPrice = item.Price,
            PriceBasis = item.PriceBasis,
            VatRateBasisPoints = item.VatRateBasisPoints,
            ValidFrom = request.ValidFrom,
            ValidUntil = request.ValidUntil,
            SeriesStart = PromotionRules.SeriesStart(previous, request.ValidFrom, request.SalePrice),
            PerishableExempt = request.PerishableExempt,
            CreatedBy = actorId,
            CreatedAt = clock.GetUtcNow(),
        };
        await session.StoreAsync(sale, changeVector: string.Empty, sale.Id, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        return sale;
    }

    public async Task<PromotionSalePrice?> CancelAsync(string salePriceId, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        var sale = await session.LoadAsync<PromotionSalePrice>(salePriceId, cancellationToken);
        if (sale is null || sale.Cancelled)
            return sale;
        var now = clock.GetUtcNow();
        if (sale.ValidFrom > now)
            sale.Cancelled = true;               // never started: nothing to record
        else if (sale.ValidUntil > now)
            sale.ValidUntil = now;               // ends now: the job records the end
        await session.SaveChangesAsync(cancellationToken);
        return sale;
    }

    public async Task<PromotionSalePrice?> GetActiveAsync(string itemId, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        return (await LoadAllAsync(session, itemId, cancellationToken)).FirstOrDefault(s => s.IsActiveAt(at));
    }

    public async Task<CheckoutItem> ApplyAsync(CheckoutItem item, CancellationToken cancellationToken = default)
    {
        var sale = await GetActiveAsync(PromotionItemIds.Inner(item.ItemId), clock.GetUtcNow(), cancellationToken);
        return sale is not null && sale.SalePrice.Currency == item.Price.Currency && sale.SalePrice < item.Price
            ? item with { Price = sale.SalePrice }
            : item;
    }

    public async Task<SparkSalePriceDisplay> DisplayAsync(ISellableItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var now = clock.GetUtcNow();
        var sale = item.Id is { } id ? await GetActiveAsync(id, now, cancellationToken) : null;
        if (sale is null || item.Price is not { } regular || sale.SalePrice.Currency != regular.Currency || sale.SalePrice >= regular)
        {
            var plain = await display.DisplayAsync(item, null, cancellationToken);
            return new SparkSalePriceDisplay(item.Id ?? "", plain.Price.UnitTotal, false, false, null, null, null, false, null, null);
        }

        var onSale = new PricedSellable(item.Id, item.Channel, item.OrganizationId, sale.SalePrice, item.PriceBasis, item.VatRateBasisPoints);
        // PID Art. 6a: a reduction is shown with the lowest final price of the 30 days before the series started.
        var listing = await display.DisplayAsync(onSale, sale.PerishableExempt ? null : sale.SeriesStart, cancellationToken);
        var regularFinal = calculator.Calculate(FinalPriceRequest.For(item)).UnitTotal;
        var prior = sale.PerishableExempt ? null : listing.Price.PriorPrice;
        var percent = PromotionRules.ReductionPercent(prior, listing.Price.UnitTotal);
        // Without a prior price (no history in the window) the reduction is not announced: the sale price is shown as the
        // price, with no strike-through, badge or percentage. An exempt item may announce it without one (Art. 6a(3)).
        var announce = sale.PerishableExempt || prior is not null;
        return new SparkSalePriceDisplay(item.Id ?? "", listing.Price.UnitTotal, true, announce, sale.ValidUntil,
            announce ? regularFinal : null, prior, sale.PerishableExempt, announce ? percent : null, sale.SeriesStart);
    }

    /// <summary>Every sale of an item, by id prefix (no index needed).</summary>
    internal static async Task<List<PromotionSalePrice>> LoadAllAsync(Raven.Client.Documents.Session.IAsyncDocumentSession session, string itemId, CancellationToken cancellationToken)
    {
        var result = new List<PromotionSalePrice>();
        var start = 0;
        while (true)
        {
            var page = (await session.Advanced.LoadStartingWithAsync<PromotionSalePrice>(PromotionSalePrice.PrefixFor(itemId), start: start, pageSize: 128, token: cancellationToken)).ToList();
            result.AddRange(page);
            if (page.Count < 128)
                return result;
            start += page.Count;
        }
    }
}
