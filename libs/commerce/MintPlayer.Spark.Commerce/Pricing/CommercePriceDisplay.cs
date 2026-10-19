using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Contracts;
using MintPlayer.Spark.Commerce.Models;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Pricing;

/// <summary>
/// Builds what a storefront shows for a price (default 4, GA3, GA5): the final price with mandatory fees, the
/// breakdown, the Omnibus prior price for an announced reduction, and the seller's trader label. Storefront packages
/// (Shop, Ticketing) call it after they loaded an item the caller may read, and put the result in their own response:
/// Commerce never loads app items by an id from the request, so it cannot leak an unpublished item's price.
/// </summary>
public interface ICommercePriceDisplay
{
    /// <summary>The display of <paramref name="item"/>; with <paramref name="announcedReductionSince"/> (when the
    /// current reduction started) the prior price is looked up, otherwise none is shown.</summary>
    Task<CommerceListingInfo> DisplayAsync(ISellableItem item, DateTimeOffset? announcedReductionSince = null, CancellationToken cancellationToken = default);

    /// <summary>The display of a raw price in a channel (no item, no prior price).</summary>
    CommercePriceDisplay Quote(FinalPriceRequest request);
}

internal sealed class CommercePriceDisplayService(
    IFinalPriceCalculator calculator,
    ISparkPriceHistory history,
    IDocumentStore store) : ICommercePriceDisplay
{
    public async Task<CommerceListingInfo> DisplayAsync(ISellableItem item, DateTimeOffset? announcedReductionSince = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var display = Quote(FinalPriceRequest.For(item));
        if (announcedReductionSince is { } since && item.Id is { } id)
        {
            // PID Art. 6a: a reduction is never shown without its prior price. Progressive reductions pass the start
            // of the first step, so they keep its reference.
            var prior = await history.GetLowestFinalPriceAsync(id, since, cancellationToken: cancellationToken);
            display = display with { PriorPrice = prior };
        }
        return new CommerceListingInfo(item.Id ?? "", item.Channel, display, await LabelAsync(item.OrganizationId, cancellationToken));
    }

    public CommercePriceDisplay Quote(FinalPriceRequest request)
        => ToDisplay(calculator.Calculate(request), request.VatRateBasisPoints is not null);

    internal static CommercePriceDisplay ToDisplay(FinalPrice price, bool hasVat)
        => new(price.UnitTotal, price.Gross, hasVat ? price.Vat : null,
            price.MandatoryFees.Select(Line).ToArray(), price.OptionalFees.Select(Line).ToArray(), null);

    private static CommerceFeeLine Line(PriceComponent c) => new(c.Code, c.Kind.ToString(), c.Amount, c.LabelKey);

    private async Task<CommerceSellerLabel> LabelAsync(string? organizationId, CancellationToken ct)
    {
        if (organizationId is null)
            return new CommerceSellerLabel(null, null, nameof(TraderStatus.Trader)); // the platform sells as a trader
        using var session = store.OpenAsyncSession();
        var profile = await session.LoadAsync<SellerProfile>(SellerProfile.IdFor(organizationId), ct);
        var status = profile?.TraderStatus ?? TraderStatus.Undeclared;
        return new CommerceSellerLabel(organizationId, status == TraderStatus.Trader ? profile!.Trader.Name : null, status.ToString());
    }
}
