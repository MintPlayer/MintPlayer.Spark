using MintPlayer.Spark.Commerce.Shipping.Models;

namespace MintPlayer.Spark.Commerce.Shipping;

/// <summary>
/// A host's override of one carrier service (<c>Spark:Commerce:Shipping:{Carrier}:Services:{code}</c>). Carrier rules
/// depend on the host's contract (dangerous goods above all, GC9), so adapters ship conservative defaults and the host
/// states what its contract allows. A code the adapter does not ship adds a service.
/// </summary>
public sealed class CarrierServiceOptions
{
    /// <summary>False hides a shipped service.</summary>
    public bool Enabled { get; set; } = true;

    public string? Name { get; set; }

    /// <summary>Destination countries; replaces the shipped list when set.</summary>
    public List<string>? Countries { get; set; }

    /// <summary>Origin countries; replaces the shipped list when set.</summary>
    public List<string>? FromCountries { get; set; }

    public int? MinWeightGrams { get; set; }

    public int? MaxWeightGrams { get; set; }

    public bool? ServicePoint { get; set; }

    public bool? Return { get; set; }

    public CustomsDocumentStyle? CustomsStyle { get; set; }

    /// <summary>The dangerous-goods classes the host's contract allows on this service (GC9); none when unset.</summary>
    public List<DangerousGoodsClass>? AcceptedDangerousGoods { get; set; }
}

/// <summary>Builds a carrier's effective service list from its shipped defaults and the host's overrides.</summary>
public static class CarrierCatalog
{
    public static IReadOnlyList<CarrierService> Apply(IEnumerable<CarrierService> shipped, IReadOnlyDictionary<string, CarrierServiceOptions>? configured)
    {
        var result = new List<CarrierService>();
        var overrides = configured ?? new Dictionary<string, CarrierServiceOptions>();
        foreach (var service in shipped)
        {
            var match = overrides.FirstOrDefault(o => string.Equals(o.Key, service.Code, StringComparison.OrdinalIgnoreCase));
            if (match.Value is null)
            {
                result.Add(service);
                continue;
            }
            if (match.Value.Enabled)
                result.Add(Merge(service, match.Value));
        }
        foreach (var (code, options) in overrides)
        {
            if (options.Enabled && !result.Any(s => string.Equals(s.Code, code, StringComparison.OrdinalIgnoreCase))
                && !shipped.Any(s => string.Equals(s.Code, code, StringComparison.OrdinalIgnoreCase)))
                result.Add(Merge(new CarrierService(code, options.Name ?? code), options));
        }
        return result;
    }

    private static CarrierService Merge(CarrierService service, CarrierServiceOptions options) => service with
    {
        Name = options.Name ?? service.Name,
        Countries = options.Countries?.Select(c => c.ToUpperInvariant()).ToList() ?? service.Countries,
        FromCountries = options.FromCountries?.Select(c => c.ToUpperInvariant()).ToList() ?? service.FromCountries,
        MinWeightGrams = options.MinWeightGrams ?? service.MinWeightGrams,
        MaxWeightGrams = options.MaxWeightGrams ?? service.MaxWeightGrams,
        ServicePoint = options.ServicePoint ?? service.ServicePoint,
        Return = options.Return ?? service.Return,
        CustomsStyle = options.CustomsStyle ?? service.CustomsStyle,
        AcceptedDangerousGoods = options.AcceptedDangerousGoods?.Distinct().ToList() ?? service.AcceptedDangerousGoods,
    };
}
