using System.Collections.Concurrent;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.DateTimes;
using MintPlayer.Spark.ReferenceData.Contracts;

namespace MintPlayer.Spark.ReferenceData.Client;

/// <summary><c>Spark:ReferenceData:Client</c> (PRD §3.3, §4.15).</summary>
public sealed class SparkReferenceDataClientOptions : ISparkConfigurationSection
{
    public static string SectionName => "Spark:ReferenceData:Client";

    /// <summary>
    /// The instance's absolute http(s) URL. <b>No default</b>: unset means free-text mode (RD-DEF 8), where addresses
    /// are checked for shape only and flagged unverified.
    /// </summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>The instance's release-signing public key (RD-DEF 5). Verification waits for spike RS17.</summary>
    public string? PublicKey { get; set; }

    public ReferenceDataSyncOptions Sync { get; set; } = new();

    /// <summary>The HTTP timeout of API calls (suggestions, profiles, zone names).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    public HouseNumberValidationOptions HouseNumbers { get; set; } = new();
}

public sealed class ReferenceDataSyncOptions
{
    /// <summary>Sync releases into this database (RD6). Off by default.</summary>
    public bool Enabled { get; set; }

    /// <summary>A 6-field cron (UTC): the conditional GET on the manifest.</summary>
    public string Schedule { get; set; } = "0 15 4 * * *";

    /// <summary>Lower-case country codes to copy; empty = every country of the release.</summary>
    public List<string> Countries { get; set; } = [];

    /// <summary>The collections to copy (streets come from the live API, §4.7).</summary>
    public List<string> Collections { get; set; } = ["RefCountries", "RefPlaces", "RefPostalCodes", "RefTimeZones", "PublicHolidays", "VatRateSuggestions"];
}

public sealed class HouseNumberValidationOptions
{
    /// <summary>Check house numbers with the registered <see cref="IHouseNumberValidator"/> (RD5). Off by default.</summary>
    public bool Enabled { get; set; }

    /// <summary>A validator slower than this accepts the value, flagged unverified (never blocking).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(2);
}

/// <summary>The typed <c>/api/v1</c> client (§4.15). Every call returns <see langword="null"/> in free-text mode.</summary>
public interface IReferenceDataClient
{
    bool IsConfigured { get; }
    Task<SuggestResponse?> SuggestAsync(string q, string? country = null, string? lang = null, DateOnly? asOf = null, int? limit = null, CancellationToken cancellationToken = default);
    Task<StreetSuggestResponse?> StreetsAsync(string q, string? municipality, string? postalCode, string? lang = null, int? limit = null, CancellationToken cancellationToken = default);
    Task<PlaceResponse?> PlaceAsync(string id, DateOnly? asOf = null, string? lang = null, CancellationToken cancellationToken = default);
    Task<ByCodeResponse?> ByCodeAsync(string country, string scheme, string code, DateOnly? asOf = null, CancellationToken cancellationToken = default);
    Task<CountryProfileResponse?> ProfileAsync(string country, CancellationToken cancellationToken = default);
    Task<TimeZonesResponse?> TimeZonesAsync(string lang, CancellationToken cancellationToken = default);
}

internal sealed class ReferenceDataClient(IHttpClientFactory httpClients, IOptions<SparkReferenceDataClientOptions> options) : IReferenceDataClient
{
    public const string HttpClientName = "MintPlayer.Spark.ReferenceData.Client";

    private readonly ConcurrentDictionary<string, Task<CountryProfileResponse?>> profiles = new(StringComparer.OrdinalIgnoreCase);

    public bool IsConfigured => options.Value.BaseAddress is not null;

    public Task<SuggestResponse?> SuggestAsync(string q, string? country, string? lang, DateOnly? asOf, int? limit, CancellationToken cancellationToken)
        => GetAsync<SuggestResponse>("suggest", cancellationToken, ("q", q), ("country", country), ("lang", lang), ("asOf", Date(asOf)), ("limit", limit?.ToString()));

    public Task<StreetSuggestResponse?> StreetsAsync(string q, string? municipality, string? postalCode, string? lang, int? limit, CancellationToken cancellationToken)
        => GetAsync<StreetSuggestResponse>("streets/suggest", cancellationToken, ("q", q), ("municipality", municipality), ("postalCode", postalCode), ("lang", lang), ("limit", limit?.ToString()));

    public Task<PlaceResponse?> PlaceAsync(string id, DateOnly? asOf, string? lang, CancellationToken cancellationToken)
        => RefIds.IsValid(id, RefIds.Places)
            ? GetAsync<PlaceResponse>("places/" + string.Join('/', id.Split('/').Skip(1).Select(Uri.EscapeDataString)), cancellationToken, ("asOf", Date(asOf)), ("lang", lang))
            : Task.FromResult<PlaceResponse?>(null);

    public Task<ByCodeResponse?> ByCodeAsync(string country, string scheme, string code, DateOnly? asOf, CancellationToken cancellationToken)
        => GetAsync<ByCodeResponse>($"by-code/{Uri.EscapeDataString(country)}/{Uri.EscapeDataString(scheme)}/{Uri.EscapeDataString(code)}", cancellationToken, ("asOf", Date(asOf)));

    /// <summary>Cached for the process: a profile changes only with a release, and a restart picks it up.</summary>
    public Task<CountryProfileResponse?> ProfileAsync(string country, CancellationToken cancellationToken)
        => profiles.GetOrAdd(country, c => GetAsync<CountryProfileResponse>($"countries/{Uri.EscapeDataString(c)}/profile", CancellationToken.None));

    public Task<TimeZonesResponse?> TimeZonesAsync(string lang, CancellationToken cancellationToken)
        => GetAsync<TimeZonesResponse>("timezones", cancellationToken, ("lang", lang));

    private async Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken, params (string Name, string? Value)[] query) where T : class
    {
        if (options.Value.BaseAddress is not { } baseAddress)
            return null;
        var url = new Uri(baseAddress, "api/v1/" + path + Query(query));
        using var response = await httpClients.CreateClient(HttpClientName).GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
    }

    private static string Query((string Name, string? Value)[] query)
    {
        var parts = query.Where(q => !string.IsNullOrEmpty(q.Value)).Select(q => $"{q.Name}={Uri.EscapeDataString(q.Value!)}").ToList();
        return parts.Count == 0 ? string.Empty : "?" + string.Join('&', parts);
    }

    private static string? Date(DateOnly? day) => day?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Step 1 of the core zone-label chain (D36): the instance's CLDR exemplar city ("Brussel"). One request per language
/// for the process (<c>/api/v1/timezones?lang=</c>), so rendering never waits on it twice; <see langword="null"/> when
/// CLDR states no city (the core then derives it from the id) or in free-text mode.
/// </summary>
internal sealed class ReferenceDataTimeZoneNameProvider(IReferenceDataClient client, ILogger<ReferenceDataTimeZoneNameProvider> logger) : ISparkTimeZoneNameProvider
{
    private readonly ConcurrentDictionary<string, Lazy<Task<Dictionary<string, string>>>> languages = new(StringComparer.OrdinalIgnoreCase);

    public async ValueTask<string?> GetCityNameAsync(string ianaZoneId, string language, CancellationToken cancellationToken)
    {
        if (!client.IsConfigured || string.IsNullOrWhiteSpace(language))
            return null;
        var names = await languages.GetOrAdd(language, lang => new Lazy<Task<Dictionary<string, string>>>(() => LoadAsync(lang))).Value;
        return names.GetValueOrDefault(ianaZoneId);
    }

    private async Task<Dictionary<string, string>> LoadAsync(string language)
    {
        try
        {
            return (await client.TimeZonesAsync(language, CancellationToken.None))?.Names ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            // Remembered as "no names" for the process: the fallback renders, and the provider is not asked per label.
            logger.LogWarning(ex, "Reference data: time-zone names for {Language} could not be fetched.", language);
            return [];
        }
    }
}
