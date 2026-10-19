using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.DateTimes;

namespace MintPlayer.Spark.Services;

/// <summary>
/// <see cref="ISparkTimeZoneNames"/>: the provider's translated city when one is registered, else the
/// English city from the IANA id (D36; the server has no platform step, S23). Cached per (zone, language).
/// </summary>
[Register(typeof(ISparkTimeZoneNames), ServiceLifetime.Singleton)]
internal partial class SparkTimeZoneNames : ISparkTimeZoneNames
{
    [Inject] private readonly ISparkTimeZoneNameProvider? provider;
    [Inject] private readonly ILogger<SparkTimeZoneNames>? logger;

    private readonly ConcurrentDictionary<(string Zone, string Language), string> cache = new();

    public async ValueTask<string> GetLabelAsync(string ianaZoneId, string language, CancellationToken cancellationToken = default)
    {
        var zone = (ianaZoneId ?? string.Empty).Trim();
        var key = (zone, (language ?? string.Empty).Trim().ToLowerInvariant());
        if (cache.TryGetValue(key, out var cached))
            return cached;

        if (provider is not null && zone.Length > 0)
        {
            try
            {
                var city = await provider.GetCityNameAsync(zone, key.Item2, cancellationToken);
                if (!string.IsNullOrWhiteSpace(city))
                    return cache[key] = city.Trim();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Not cached: the source may be back on the next call. The fallback still renders.
                logger?.LogWarning(ex, "The time-zone name provider failed for {TimeZoneId}; using the English city.", zone);
                return SparkTimeZones.CityFromId(zone);
            }
        }

        return cache[key] = SparkTimeZones.CityFromId(zone);
    }
}
