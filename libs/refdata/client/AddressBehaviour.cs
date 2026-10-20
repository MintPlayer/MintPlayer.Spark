using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.ReferenceData.Contracts;

namespace MintPlayer.Spark.ReferenceData.Client;

/// <summary>Renders an address's lines from the country profile's template (RD-DEF 6).</summary>
public static partial class AddressLines
{
    [GeneratedRegex(@"\[([^\[\]]*)\]")]
    private static partial Regex Optional();

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex Placeholder();

    /// <summary>The generic template when no profile is known: street line, then postcode and locality, then the country code.</summary>
    public static readonly IReadOnlyList<string> Fallback = ["{street} {houseNumber}[ {box}]", "{postalCode} {locality}"];

    /// <summary>
    /// Each template line with its placeholders filled. An optional part <c>[ bus {box}]</c> is dropped when its values are
    /// empty; a line left empty is dropped; whitespace is collapsed.
    /// </summary>
    public static List<string> Render(Address address, IReadOnlyList<string>? template)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["street"] = address.StreetName,
            ["houseNumber"] = address.HouseNumber,
            ["box"] = address.Box,
            ["postalCode"] = address.PostalCode,
            ["locality"] = address.LocalityName ?? address.MunicipalityName,
            ["municipality"] = address.MunicipalityName,
        };
        var lines = new List<string>();
        foreach (var line in template is { Count: > 0 } ? template : Fallback)
        {
            var withOptional = Optional().Replace(line, m =>
                Placeholder().Matches(m.Groups[1].Value).All(p => !string.IsNullOrWhiteSpace(values.GetValueOrDefault(p.Groups[1].Value)))
                    ? m.Groups[1].Value
                    : string.Empty);
            var anyValue = Placeholder().Matches(withOptional).Any(p => !string.IsNullOrWhiteSpace(values.GetValueOrDefault(p.Groups[1].Value)));
            var filled = Placeholder().Replace(withOptional, p => values.GetValueOrDefault(p.Groups[1].Value)?.Trim() ?? string.Empty);
            var collapsed = string.Join(' ', filled.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim(' ', ',');
            if (anyValue && collapsed.Length > 0)
                lines.Add(collapsed);
        }
        return lines;
    }
}

/// <summary>
/// The optional house-number hook (RD5). <b>Never blocking</b>: a timeout or an error accepts the value and flags it
/// unverified. One implementation ships, <see cref="BestHouseNumberValidator"/>, as an example for hosts.
/// </summary>
public interface IHouseNumberValidator
{
    /// <summary>Countries (lower case) this validator knows.</summary>
    bool Handles(string country);

    /// <summary><see langword="true"/> = confirmed, <see langword="false"/> = the register does not know it, <see langword="null"/> = could not tell.</summary>
    Task<bool?> ValidateAsync(Address address, CancellationToken cancellationToken);
}

/// <summary>
/// Belgium's free, keyless BeST API (PRD §4.14): <c>/addresses</c> filtered by street id and house number
/// (https://best.pr.fedservices.be/api/opendata/best/v1/belgianAddress/v2/doc/openapi.yaml). "Zonder garanties": an
/// example for hosts. ⚠️ The query parameters follow the published OpenAPI document; RS15 records a real response.
/// </summary>
public sealed class BestHouseNumberValidator(IHttpClientFactory httpClients) : IHouseNumberValidator
{
    public const string HttpClientName = "MintPlayer.Spark.ReferenceData.Client.BeST";
    public const string Endpoint = "https://best.pr.fedservices.be/api/opendata/best/v1/belgianAddress/v2/addresses";

    public bool Handles(string country) => country == "be";

    public async Task<bool?> ValidateAsync(Address address, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(address.HouseNumber) || string.IsNullOrWhiteSpace(address.PostalCode) || string.IsNullOrWhiteSpace(address.StreetName))
            return null;
        var url = $"{Endpoint}?postCode={Uri.EscapeDataString(address.PostalCode)}&streetName={Uri.EscapeDataString(address.StreetName)}&houseNumber={Uri.EscapeDataString(address.HouseNumber)}";
        if (!string.IsNullOrWhiteSpace(address.Box))
            url += "&boxNumber=" + Uri.EscapeDataString(address.Box);
        using var response = await httpClients.CreateClient(HttpClientName).GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;
        using var json = await System.Text.Json.JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return json.RootElement.TryGetProperty("items", out var items) && items.ValueKind == System.Text.Json.JsonValueKind.Array
            ? items.GetArrayLength() > 0
            : null;
    }
}

/// <summary>
/// The <see cref="Address"/> behaviour (PRD §4.15): an untyped before-save interceptor over every entity holding an
/// <see cref="Address"/> (directly or in a list). It refuses invalid fields with <see cref="SparkValidationException"/>,
/// renders <see cref="Address.Lines"/> from the country profile, takes the time zone from the municipality (D33), and
/// asks the house-number hook without ever blocking. In free-text mode (no <c>BaseAddress</c>) it checks shape only
/// and marks the address unverified.
/// </summary>
internal sealed class AddressInterceptor(
    IReferenceDataClient client,
    IEnumerable<IHouseNumberValidator> validators,
    IOptions<SparkReferenceDataClientOptions> options,
    ILogger<AddressInterceptor> logger) : IBeforeSave
{
    private static readonly Dictionary<Type, PropertyInfo[]> AddressProperties = [];

    public bool AppliesTo(Type entityType) => PropertiesOf(entityType).Length > 0;

    public async ValueTask OnBeforeSaveAsync(SaveContext context)
    {
        foreach (var property in PropertiesOf(context.Entity.GetType()))
        {
            switch (property.GetValue(context.Entity))
            {
                case Address address:
                    await NormalizeAsync(address, property.Name);
                    break;
                case IEnumerable list:
                    foreach (var item in list.OfType<Address>())
                        await NormalizeAsync(item, property.Name);
                    break;
            }
        }
    }

    private async Task NormalizeAsync(Address address, string attribute)
    {
        if (IsEmpty(address))
            return;
        var country = (address.CountryCode ?? string.Empty).Trim().ToUpperInvariant();
        if (!RefIds.IsCountryCode(country))
            throw new SparkValidationException("The country must be an ISO 3166-1 alpha-2 code.", attribute + ".CountryCode");
        address.CountryCode = country;
        foreach (var (value, name, max) in new[] { (address.StreetName, "StreetName", 200), (address.HouseNumber, "HouseNumber", 16), (address.Box, "Box", 16), (address.PostalCode, "PostalCode", 16), (address.LocalityName, "LocalityName", 200) })
            if (value is { Length: > 0 } && (value.Length > max || value.Any(char.IsControl)))
                throw new SparkValidationException($"{name} is too long or holds control characters.", $"{attribute}.{name}");
        foreach (var (id, name, collection) in new[] { (address.StreetId, "StreetId", RefIds.Streets), (address.PostalCodeId, "PostalCodeId", RefIds.PostalCodes), (address.MunicipalityId, "MunicipalityId", RefIds.Places), (address.LocalityPlaceId, "LocalityPlaceId", RefIds.Places) })
            if (!string.IsNullOrEmpty(id) && !RefIds.IsValid(id, collection))
                throw new SparkValidationException($"{name} is not a reference-data id.", $"{attribute}.{name}");

        CountryProfileResponse? profile = null;
        if (client.IsConfigured)
        {
            try { profile = await client.ProfileAsync(country.ToLowerInvariant()); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { logger.LogWarning(ex, "Reference data unreachable; address checked for shape only."); }
        }

        if (profile is not null)
        {
            if (!string.IsNullOrEmpty(address.PostalCode) && profile.PostcodePattern is { } postcode && !Regex.IsMatch(address.PostalCode.Trim(), postcode, RegexOptions.None, TimeSpan.FromMilliseconds(100)))
                throw new SparkValidationException("The postal code does not have this country's format.", attribute + ".PostalCode");
            if (!string.IsNullOrEmpty(address.HouseNumber) && profile.HouseNumberPattern is { } number && !Regex.IsMatch(address.HouseNumber.Trim(), number, RegexOptions.None, TimeSpan.FromMilliseconds(100)))
                throw new SparkValidationException("The house number does not have this country's format.", attribute + ".HouseNumber");
            if (!string.IsNullOrEmpty(address.Box) && (profile.BoxPattern is null || !Regex.IsMatch(address.Box.Trim(), profile.BoxPattern, RegexOptions.None, TimeSpan.FromMilliseconds(100))))
                throw new SparkValidationException("The box does not have this country's format.", attribute + ".Box");
            address.TimeZone ??= profile.TimeZone;
        }

        if (address.MunicipalityId is { Length: > 0 } municipality && address.TimeZone is null && client.IsConfigured)
            address.TimeZone = (await client.PlaceAsync(municipality))?.Place?.TimeZone;

        address.Verified = client.IsConfigured && !string.IsNullOrEmpty(address.PostalCodeId) && !string.IsNullOrEmpty(address.MunicipalityId);
        address.Lines = AddressLines.Render(address, profile?.AddressLines);
        address.HouseNumberVerified = await CheckHouseNumberAsync(address, country.ToLowerInvariant());
    }

    private async Task<bool> CheckHouseNumberAsync(Address address, string country)
    {
        var settings = options.Value.HouseNumbers;
        if (!settings.Enabled || validators.FirstOrDefault(v => v.Handles(country)) is not { } validator)
            return false;
        using var timeout = new CancellationTokenSource(settings.Timeout);
        try
        {
            return await validator.ValidateAsync(address, timeout.Token) == true;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            // Never blocking (RD5): accepted, flagged unverified.
            logger.LogInformation(ex, "House-number validation unavailable; accepted unverified.");
            return false;
        }
    }

    private static bool IsEmpty(Address a)
        => string.IsNullOrWhiteSpace(a.CountryCode) && string.IsNullOrWhiteSpace(a.StreetName) && string.IsNullOrWhiteSpace(a.PostalCode)
           && string.IsNullOrWhiteSpace(a.LocalityName) && string.IsNullOrWhiteSpace(a.HouseNumber);

    private static PropertyInfo[] PropertiesOf(Type type)
    {
        lock (AddressProperties)
        {
            if (!AddressProperties.TryGetValue(type, out var properties))
                AddressProperties[type] = properties = [.. type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.CanRead && p.GetIndexParameters().Length == 0
                        && (p.PropertyType == typeof(Address) || typeof(IEnumerable<Address>).IsAssignableFrom(p.PropertyType)))];
            return properties;
        }
    }
}
