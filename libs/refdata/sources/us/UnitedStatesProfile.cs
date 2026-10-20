using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.ReferenceData.Sources.UnitedStates;

/// <summary>The U.S. country profile (RD-DEF 6), hand-authored (nothing copied from libaddressinput).</summary>
public static class UnitedStatesProfile
{
    /// <summary>
    /// Q-R10 for the U.S.: the zone of a state that lies in one zone, by state FIPS code; every county of it gets that
    /// zone. States split between zones (and Arizona, where the Navajo Nation observes DST) are absent: their counties get
    /// no zone, with <see cref="SplitStateNote"/>, until a per-county source is joined (GeoNames' per-feature zone).
    /// Unofficial local practice (Phenix City AL, West Wendover NV, Kenton OK) follows the legal zone.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> SingleZoneStates = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["01"] = "America/Chicago", ["05"] = "America/Chicago", ["06"] = "America/Los_Angeles", ["08"] = "America/Denver",
        ["09"] = "America/New_York", ["10"] = "America/New_York", ["11"] = "America/New_York", ["13"] = "America/New_York",
        ["15"] = "Pacific/Honolulu", ["17"] = "America/Chicago", ["19"] = "America/Chicago", ["22"] = "America/Chicago",
        ["23"] = "America/New_York", ["24"] = "America/New_York", ["25"] = "America/New_York", ["27"] = "America/Chicago",
        ["28"] = "America/Chicago", ["29"] = "America/Chicago", ["30"] = "America/Denver", ["32"] = "America/Los_Angeles",
        ["33"] = "America/New_York", ["34"] = "America/New_York", ["35"] = "America/Denver", ["36"] = "America/New_York",
        ["37"] = "America/New_York", ["39"] = "America/New_York", ["40"] = "America/Chicago", ["42"] = "America/New_York",
        ["44"] = "America/New_York", ["45"] = "America/New_York", ["49"] = "America/Denver", ["50"] = "America/New_York",
        ["51"] = "America/New_York", ["53"] = "America/Los_Angeles", ["54"] = "America/New_York", ["55"] = "America/Chicago",
        ["56"] = "America/Denver", ["60"] = "Pacific/Pago_Pago", ["66"] = "Pacific/Guam", ["69"] = "Pacific/Saipan",
        ["72"] = "America/Puerto_Rico", ["78"] = "America/St_Thomas",
    };

    public const string SplitStateNote = "The state lies in several time zones; the county's zone is not derived from the Census files (Q-R10).";

    public static CountryProfile Create() => new()
    {
        Levels =
        [
            RefAdapterKit.Level("us:country", PlaceRole.Country, 0, 1, false, ("en", "Country"), ("es", "País"), ("de", "Staat"), ("fr", "Pays"), ("nl", "Land")),
            RefAdapterKit.Level("us:state", PlaceRole.Region, 1, 3, false, ("en", "State"), ("es", "Estado"), ("de", "Bundesstaat"), ("fr", "État"), ("nl", "Staat")),
            // Counties and county equivalents (parishes, boroughs, census areas, independent cities, planning regions):
            // the finest unit the imported Census files carry. Incorporated places are not imported (yet).
            RefAdapterKit.Level("us:county", PlaceRole.Municipality, 2, 5, false, ("en", "County"), ("es", "Condado"), ("de", "County"), ("fr", "Comté"), ("nl", "County")),
        ],
        MunicipalityLevel = "us:county",
        OfficialLanguages = [new() { Lang = "en" }],
        AddressLines = ["{houseNumber} {street}", "{locality} {postalCode}"],
        // ZIP or ZIP+4. Census-derived ZIP Codes are not authoritative (see the source's credit line).
        PostcodePattern = @"^[0-9]{5}(-[0-9]{4})?$",
        HouseNumbers = new HouseNumberRules { NumberPattern = @"^[0-9]{1,6}[A-Za-z]?(-[0-9A-Za-z]{1,5})?$", BoxPattern = @"^[0-9A-Za-z#-]{1,8}$" },
        TypeaheadSteps = ["postalCode", "street", "houseNumber"],
        // The Census Bureau's county-changes pages start in 1970.
        HistoryKnownSince = new DateOnly(1970, 1, 1),
        TimeZone = null,
        // Not an EU member state.
        VatTerritories = [],
    };
}
