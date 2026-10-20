using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.ReferenceData.Sources.Australia;

/// <summary>The Australian country profile (RD-DEF 6), hand-authored (nothing copied from libaddressinput).</summary>
public static class AustraliaProfile
{
    /// <summary>
    /// Q-R10 for Australia: the zone of each state by its G-NAF abbreviation. <c>OT</c> (Jervis Bay Territory) follows
    /// New South Wales; Christmas Island and the Cocos (Keeling) Islands have their own ISO codes and zones.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> StateZones = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ACT"] = "Australia/Sydney",
        ["NSW"] = "Australia/Sydney",
        ["VIC"] = "Australia/Melbourne",
        ["QLD"] = "Australia/Brisbane",
        ["SA"] = "Australia/Adelaide",
        ["WA"] = "Australia/Perth",
        ["TAS"] = "Australia/Hobart",
        ["NT"] = "Australia/Darwin",
        ["OT"] = "Australia/Sydney",
    };

    /// <summary>
    /// Localities in another zone than their state, by postcode: Broken Hill (NSW, 2880) keeps South Australian time and
    /// Lord Howe Island (NSW, 2898) has its own half-hour daylight saving.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> PostcodeZones = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["2880"] = "Australia/Broken_Hill",
        ["2898"] = "Australia/Lord_Howe",
    };

    public static CountryProfile Create() => new()
    {
        Levels =
        [
            RefAdapterKit.Level("au:country", PlaceRole.Country, 0, 1, false, ("en", "Country"), ("de", "Staat"), ("fr", "Pays"), ("nl", "Land")),
            RefAdapterKit.Level("au:state", PlaceRole.Region, 1, 3, false, ("en", "State or territory"), ("de", "Bundesstaat oder Territorium"), ("fr", "État ou territoire"), ("nl", "Staat of territorium")),
            // The ACT's districts (G-NAF locality class D); absent elsewhere.
            RefAdapterKit.Level("au:district", PlaceRole.Subregion, 2, 2, true, ("en", "District"), ("de", "Distrikt"), ("fr", "District"), ("nl", "District")),
            // The suburb or locality: the city-line unit. Local government areas (Geoscape/ABS) are not imported.
            RefAdapterKit.Level("au:locality", PlaceRole.Municipality, 3, 7, false, ("en", "Suburb or locality"), ("de", "Ort"), ("fr", "Localité"), ("nl", "Plaats")),
        ],
        MunicipalityLevel = "au:locality",
        OfficialLanguages = [new() { Lang = "en" }],
        // "3 Allambee Street" / "Reid 2612"; a unit is written before the number ("2/134 Aranda Street").
        AddressLines = ["[{box}/]{houseNumber} {street}", "{locality} {postalCode}"],
        PostcodePattern = @"^[0-9]{4}$",
        HouseNumbers = new HouseNumberRules { NumberPattern = @"^[0-9]{1,5}[A-Za-z]?(-[0-9]{1,5}[A-Za-z]?)?$", BoxPattern = @"^[0-9A-Za-z]{1,7}$" },
        TypeaheadSteps = ["locality", "street", "houseNumber"],
        // G-NAF is a current-state product (retired records leave the release): history is known from the first import.
        HistoryKnownSince = null,
        // Several zones: places carry their own (Q-R10).
        TimeZone = null,
        // Not an EU member state.
        VatTerritories = [],
    };
}
