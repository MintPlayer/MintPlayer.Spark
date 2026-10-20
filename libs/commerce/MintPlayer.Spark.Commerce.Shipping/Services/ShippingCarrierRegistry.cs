using Microsoft.Extensions.Options;

namespace MintPlayer.Spark.Commerce.Shipping.Services;

/// <summary>The registered carriers and service-point providers, by name.</summary>
internal sealed class ShippingCarrierRegistry(
    IEnumerable<IShippingCarrier> carriers,
    IEnumerable<IServicePointProvider> servicePoints,
    IOptionsMonitor<SparkCommerceShippingOptions> options)
{
    private readonly IReadOnlyList<IShippingCarrier> all = carriers.ToList();
    private readonly IReadOnlyList<IServicePointProvider> points = servicePoints.ToList();

    public IReadOnlyList<IShippingCarrier> All => all;

    /// <summary>The carrier <paramref name="name"/>; the configured default, or the only one, when null.</summary>
    public IShippingCarrier Get(string? name)
    {
        var wanted = name is { Length: > 0 } ? name : options.CurrentValue.Carrier;
        if (wanted is { Length: > 0 })
            return all.FirstOrDefault(c => string.Equals(c.Name, wanted, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"No carrier '{wanted}' is registered.");
        return all.Count == 1 ? all[0] : throw new InvalidOperationException(all.Count == 0
            ? "No carrier is registered: add UseSendcloud(), UseBpost(), UsePostNL() or UseTestCarrier()."
            : "Several carriers are registered: name one, or set Spark:Commerce:Shipping:Carrier.");
    }

    public IShippingCarrier? Find(string? name)
        => name is { Length: > 0 } ? all.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) : null;

    /// <summary>The service-point provider of <paramref name="carrier"/>, or null.</summary>
    public IServicePointProvider? ServicePoints(string carrier)
        => points.FirstOrDefault(p => string.Equals(p.Carrier, carrier, StringComparison.OrdinalIgnoreCase));
}

/// <summary>The webhook source names of carriers: <c>shipping-{carrier}</c>.</summary>
public static class ShippingWebhookSources
{
    public const string Prefix = "shipping-";

    public static string For(string carrier) => Prefix + carrier.ToLowerInvariant();

    public static string? CarrierOf(string source)
        => source.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && source.Length > Prefix.Length ? source[Prefix.Length..] : null;
}
