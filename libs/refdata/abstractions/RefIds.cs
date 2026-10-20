using System.Globalization;
using System.Text.RegularExpressions;

namespace MintPlayer.Spark.ReferenceData;

/// <summary>
/// The id grammar (PRD §4.3, RD-DEF 12): <c>{collection}/{country}/{scheme}/{code}[@{yyyy-MM-dd}]</c>.
/// <list type="bullet">
/// <item>Ids are case-insensitive; this class writes them lower case, codes keep their case
/// (<c>places/be/nis6/45017A</c>) because RavenDB compares ids case-insensitively anyway.</item>
/// <item>Every segment is validated; no id ends in <c>/</c> or <c>|</c> (which asks the server to assign one).</item>
/// <item>Consumers never compute ids from codes: an id records the code that <em>minted</em> it, and a recode keeps
/// the old one. They ask <c>/api/v1/by-code</c> or the local <c>ByCode</c> lookup instead.</item>
/// </list>
/// Shared by the instance and the client, so both refuse the same malformed id.
/// </summary>
public static partial class RefIds
{
    public const string Places = "places";
    public const string PostalCodes = "postalcodes";
    public const string Streets = "streets";
    public const string Lineage = "lineage";
    public const string Countries = "countries";
    public const string TimeZones = "timezones";
    public const string Holidays = "holidays";
    public const string VatRates = "vatrates";
    public const string Releases = "refreleases";

    [GeneratedRegex(@"^[a-z]{2}$")]
    private static partial Regex CountryPattern();

    // Code spaces and codes: letters, digits and . _ - only. No slash (it separates segments), no '|', no '@'.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-]{0,63}$")]
    private static partial Regex SegmentPattern();

    [GeneratedRegex(@"^(places|postalcodes|streets|lineage|countries|timezones|holidays|vatrates|refreleases)/[A-Za-z0-9][A-Za-z0-9._\-/]{0,190}(@\d{4}-\d{2}-\d{2})?$")]
    private static partial Regex IdPattern();

    /// <summary><c>places/{cc}/{scheme}/{code}</c>, with <c>@{date}</c> only for a code an authority reissued to a different unit.</summary>
    public static string Place(string country, string scheme, string code, DateOnly? reissuedOn = null)
        => Join(Places, Country(country), Segment(scheme).ToLowerInvariant(), Segment(code)) + Suffix(reissuedOn);

    public static string PostalCode(string country, string code) => Join(PostalCodes, Country(country), Segment(code));

    /// <summary><c>streets/{cc}/{namespace}/{objectId}</c>: the region prefix keeps Brussels and Flemish ids apart.</summary>
    public static string Street(string country, string @namespace, string objectId)
        => Join(Streets, Country(country), Segment(@namespace).ToLowerInvariant(), Segment(objectId));

    public static string LineageEvent(string country, DateOnly date, LineageKind kind, string scheme, string code)
        => Join(Lineage, Country(country), date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), kind.ToString().ToLowerInvariant(), Segment(scheme).ToLowerInvariant() + "-" + Segment(code));

    public static string CountryId(string country) => Join(Countries, Country(country));

    /// <summary><c>timezones/Europe/Brussels</c>: the IANA id as-is (its segments are checked one by one).</summary>
    public static string TimeZone(string ianaZone)
    {
        var parts = ianaZone.Split('/');
        foreach (var part in parts) Segment(part);
        return TimeZones + "/" + ianaZone;
    }

    public static string Holiday(string country, string? subdivision, DateOnly date)
        => Join(Holidays, subdivision is { Length: > 0 } ? Country(country) + "-" + Segment(subdivision).ToLowerInvariant() : Country(country), date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    public static string VatRate(string country, string type, string category, DateOnly situationOn)
        => Join(VatRates, Country(country), Segment(type).ToLowerInvariant(), Segment(category).ToLowerInvariant(), situationOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    /// <summary>Whether <paramref name="id"/> is a well-formed reference-data id (request input is checked with this before any query).</summary>
    public static bool IsValid(string? id)
        => id is { Length: > 0 and <= 200 } && IdPattern().IsMatch(id) && !id.EndsWith('/') && !id.Contains("//", StringComparison.Ordinal);

    /// <summary>Whether <paramref name="id"/> is a valid id in <paramref name="collection"/>.</summary>
    public static bool IsValid(string? id, string collection)
        => IsValid(id) && id!.StartsWith(collection + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>The country segment of an id (<c>places/be/…</c> → <c>be</c>), or <see langword="null"/>.</summary>
    public static string? CountryOf(string id)
    {
        var parts = id.Split('/');
        return parts.Length > 1 && CountryPattern().IsMatch(parts[1]) ? parts[1] : null;
    }

    /// <summary>A lower-case ISO 3166-1 alpha-2 code, or <see cref="ArgumentException"/>.</summary>
    public static string Country(string country)
    {
        var lower = (country ?? string.Empty).Trim().ToLowerInvariant();
        return CountryPattern().IsMatch(lower) ? lower : throw new ArgumentException($"'{country}' is not an ISO 3166-1 alpha-2 code.", nameof(country));
    }

    public static bool IsCountryCode(string? country) => country is not null && CountryPattern().IsMatch(country.ToLowerInvariant());

    private static string Segment(string value)
        => value is not null && SegmentPattern().IsMatch(value) ? value : throw new ArgumentException($"'{value}' is not a valid id segment.", nameof(value));

    private static string Suffix(DateOnly? date) => date is { } d ? "@" + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty;

    private static string Join(params string[] segments) => string.Join('/', segments);
}

/// <summary>
/// Integer date keys for index range predicates (§4.2): <c>yyyyMMdd</c>, <see cref="OpenStart"/> for an unbounded
/// start and <see cref="OpenEnd"/> for an unbounded end. RavenDB range predicates never match null or absent values,
/// so an open bound must be a number.
/// </summary>
public static class DateKeys
{
    public const int OpenStart = 0;
    public const int OpenEnd = 99991231;

    public static int From(DateOnly? from) => from is { } d ? Of(d) : OpenStart;

    public static int To(DateOnly? to) => to is { } d ? Of(d) : OpenEnd;

    public static int Of(DateOnly day) => day.Year * 10000 + day.Month * 100 + day.Day;

    public static DateOnly? ToDate(int key)
        => key is OpenStart or OpenEnd ? null : new DateOnly(key / 10000, key / 100 % 100, key % 100);

    /// <summary>Whether <paramref name="day"/> lies in <c>[from, to)</c>.</summary>
    public static bool Covers(int fromKey, int toKey, DateOnly day)
    {
        var key = Of(day);
        return fromKey <= key && key < toKey;
    }
}
