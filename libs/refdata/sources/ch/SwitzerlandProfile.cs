using static MintPlayer.Spark.ReferenceData.Sources.RefAdapterKit;

namespace MintPlayer.Spark.ReferenceData.Sources.Switzerland;

/// <summary>
/// The Swiss country profile (RD-DEF 6), hand-authored. Switzerland is outside the EU, so it has no EU VAT territories
/// (Büsingen and Campione, Swiss customs territory, belong to Germany's and Italy's profiles).
/// </summary>
public static class SwitzerlandProfile
{
    public const string Zone = "Europe/Zurich";

    /// <summary>The cantons' official languages, in the order their bilingual names state them (BFS: "Bern / Berne").</summary>
    internal static readonly IReadOnlyDictionary<string, string[]> CantonLanguages = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["ZH"] = ["de"], ["BE"] = ["de", "fr"], ["LU"] = ["de"], ["UR"] = ["de"], ["SZ"] = ["de"], ["OW"] = ["de"], ["NW"] = ["de"],
        ["GL"] = ["de"], ["ZG"] = ["de"], ["FR"] = ["fr", "de"], ["SO"] = ["de"], ["BS"] = ["de"], ["BL"] = ["de"], ["SH"] = ["de"],
        ["AR"] = ["de"], ["AI"] = ["de"], ["SG"] = ["de"], ["GR"] = ["de", "it", "rm"], ["AG"] = ["de"], ["TG"] = ["de"], ["TI"] = ["it"],
        ["VD"] = ["fr"], ["VS"] = ["fr", "de"], ["NE"] = ["fr"], ["GE"] = ["fr"], ["JU"] = ["fr"],
    };

    public static CountryProfile Create() => new()
    {
        Levels =
        [
            Level("ch:country", PlaceRole.Country, 0, 1, false, ("de", "Staat"), ("fr", "Pays"), ("it", "Stato"), ("rm", "Stadi"), ("en", "Country"), ("nl", "Land")),
            Level("ch:canton", PlaceRole.Region, 1, 2, false, ("de", "Kanton"), ("fr", "Canton"), ("it", "Cantone"), ("rm", "Chantun"), ("en", "Canton"), ("nl", "Kanton")),
            // Ten cantons have no districts: the BFS register's "Kanton ohne Bezirksunterteilung" rows are not places.
            Level("ch:district", PlaceRole.Subregion, 2, 2, true, ("de", "Bezirk"), ("fr", "District"), ("it", "Distretto"), ("rm", "District"), ("en", "District"), ("nl", "District")),
            Level("ch:municipality", PlaceRole.Municipality, 3, 7, false, ("de", "Gemeinde"), ("fr", "Commune"), ("it", "Comune"), ("rm", "Vischnanca"), ("en", "Municipality"), ("nl", "Gemeente")),
        ],
        MunicipalityLevel = "ch:municipality",
        OfficialLanguages = [new() { Lang = "de" }, new() { Lang = "fr" }, new() { Lang = "it" }, new() { Lang = "rm" }],
        AddressLines = ["{street} {houseNumber}", "{postalCode} {locality}"],
        PostcodePattern = @"^[1-9][0-9]{3}$",
        HouseNumbers = new HouseNumberRules { NumberPattern = @"^[1-9][0-9]{0,3}(\.[0-9]{1,2})?[A-Za-z]?$" },
        TypeaheadSteps = ["locality", "street", "houseNumber"],
        // The register's mutation history starts in 1960 (about 4,000 mutations; start year unverified, PRD §4.4).
        HistoryKnownSince = new DateOnly(1960, 1, 1),
        TimeZone = Zone,
        VatTerritories = [],
    };
}
