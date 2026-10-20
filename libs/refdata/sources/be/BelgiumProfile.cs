using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.ReferenceData.Sources.Belgium;

/// <summary>
/// The Belgian country profile (RD-DEF 6, plan R6), hand-authored: libaddressinput's data states no licence, so
/// nothing is copied from it.
/// </summary>
public static class BelgiumProfile
{
    public const string Zone = "Europe/Brussels";

    public static CountryProfile Create() => new()
    {
        Levels =
        [
            Level("be:country", PlaceRole.Country, 0, false, 1, ("nl", "Land"), ("fr", "Pays"), ("de", "Land"), ("en", "Country")),
            Level("be:region", PlaceRole.Region, 1, false, 2, ("nl", "Gewest"), ("fr", "Région"), ("de", "Region"), ("en", "Region")),
            // "The Brussels-Capital Region has none."
            Level("be:province", PlaceRole.Subregion, 2, true, 3, ("nl", "Provincie"), ("fr", "Province"), ("de", "Provinz"), ("en", "Province")),
            Level("be:arrondissement", PlaceRole.Subregion, 3, false, 2, ("nl", "Arrondissement"), ("fr", "Arrondissement"), ("de", "Bezirk"), ("en", "Arrondissement")),
            Level("be:municipality", PlaceRole.Municipality, 4, false, 7, ("nl", "Gemeente"), ("fr", "Commune"), ("de", "Gemeinde"), ("en", "Municipality")),
            // Antwerpen's districts (Borsbeek since 2025).
            Level("be:district", PlaceRole.Submunicipality, 5, true, 6, ("nl", "District"), ("fr", "District"), ("de", "Distrikt"), ("en", "District")),
            Level("be:section", PlaceRole.Submunicipality, 5, true, 6, ("nl", "Deelgemeente"), ("fr", "Section"), ("de", "Teilgemeinde"), ("en", "Section")),
            // Walloon parts of municipality (Q-R16).
            Level("be:locality", PlaceRole.Locality, 6, true, 5, ("nl", "Plaats"), ("fr", "Localité"), ("de", "Ortschaft"), ("en", "Locality")),
        ],
        MunicipalityLevel = "be:municipality",
        OfficialLanguages = [new() { Lang = "nl" }, new() { Lang = "fr" }, new() { Lang = "de" }],
        AddressLines = ["{street} {houseNumber}[ bus {box}]", "{postalCode} {locality}"],
        PostcodePattern = @"^[1-9][0-9]{3}$",
        HouseNumbers = new HouseNumberRules { NumberPattern = @"^[1-9][0-9]{0,4}[A-Za-z]?$", BoxPattern = @"^[0-9A-Za-z]{1,6}$" },
        TypeaheadSteps = ["locality", "street", "houseNumber"],
        HistoryKnownSince = new DateOnly(1970, 1, 1),
        TimeZone = Zone,
        // Belgium has no territory outside the EU VAT territory (Directive 2006/112/EC Art. 6 names none).
        VatTerritories = [],
    };

    private static AdminLevel Level(string key, PlaceRole role, int order, bool optional, int weight, params (string Tag, string Label)[] labels) => new()
    {
        Key = key,
        Role = role,
        Order = order,
        Optional = optional,
        RankWeight = weight,
        Labels = TranslatedString.Of(labels),
    };
}
