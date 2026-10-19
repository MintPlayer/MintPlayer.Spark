using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Commerce.Shipping.Rates;

/// <summary>
/// The configured methods (§4.9): a flat rate per zone, a free-shipping threshold per zone, and pickup. A method
/// without a rate for the destination's zone is not offered there.
/// </summary>
internal sealed class ConfiguredShippingRateProvider(IOptionsMonitor<SparkCommerceShippingOptions> options) : IShippingRateProvider
{
    public Task<IReadOnlyList<ShippingRate>> GetRatesAsync(ShippingRateRequest request, CancellationToken cancellationToken)
        => Task.FromResult(Rates(options.CurrentValue, request));

    internal static IReadOnlyList<ShippingRate> Rates(SparkCommerceShippingOptions settings, ShippingRateRequest request)
    {
        var zone = ShippingZones.Resolve(settings.Zones, request.ShipTo.Country);
        if (zone is null)
            return [];
        var currency = settings.Currency;
        var rates = new List<ShippingRate>();
        foreach (var (key, method) in settings.Methods)
        {
            if (!method.Rates.TryGetValue(zone, out var flat))
                continue;
            if (method.MaxWeightGrams > 0 && request.WeightGrams > method.MaxWeightGrams)
                continue;
            Money? freeFrom = method.FreeFrom.TryGetValue(zone, out var threshold) ? new Money(threshold, currency) : null;
            // The threshold compares like with like: a subtotal in another currency never makes shipping free.
            var free = freeFrom is not null
                && string.Equals(request.Subtotal.Currency, currency, StringComparison.OrdinalIgnoreCase)
                && request.Subtotal >= freeFrom;
            rates.Add(new ShippingRate
            {
                Method = key,
                Kind = method.Kind,
                Carrier = method.Kind == ShippingMethodKind.Pickup ? null : method.Carrier ?? settings.Carrier,
                ServiceCode = method.Kind == ShippingMethodKind.Pickup ? null : method.Service,
                Price = free ? Money.Zero(currency) : new Money(flat, currency),
                Free = free,
                FreeFrom = freeFrom,
                Zone = zone,
                PickupLocation = method.PickupLocation,
                Order = method.Order,
            });
        }
        return rates;
    }
}
