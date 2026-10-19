using MintPlayer.Spark.Abstractions;
using static MintPlayer.Spark.ReferenceData.Sources.RefAdapterKit;

namespace MintPlayer.Spark.ReferenceData.Sources.Netherlands;

/// <summary>
/// The Dutch country profile (RD-DEF 6, plan R8), hand-authored (libaddressinput states no licence). It replaces the
/// global baseline's hand-written NL profile.
/// </summary>
public static class NetherlandsProfile
{
    public const string Zone = "Europe/Amsterdam";

    public static CountryProfile Create() => new()
    {
        Levels =
        [
            Level("nl:country", PlaceRole.Country, 0, 1, false, ("nl", "Land"), ("en", "Country"), ("fr", "Pays"), ("de", "Staat")),
            Level("nl:province", PlaceRole.Region, 1, 2, false, ("nl", "Provincie"), ("en", "Province"), ("fr", "Province"), ("de", "Provinz")),
            Level("nl:municipality", PlaceRole.Municipality, 2, 6, false, ("nl", "Gemeente"), ("en", "Municipality"), ("fr", "Commune"), ("de", "Gemeinde")),
            // The woonplaats is the city-line unit (BAG): it ranks highest in the typeahead.
            Level("nl:woonplaats", PlaceRole.Locality, 3, 7, false, ("nl", "Woonplaats"), ("en", "Locality"), ("fr", "Localité"), ("de", "Ortschaft")),
        ],
        MunicipalityLevel = "nl:municipality",
        OfficialLanguages = [new() { Lang = "nl" }],
        AddressLines = ["{street} {houseNumber}[{box}]", "{postalCode} {locality}"],
        // A full (PC6) postcode: four digits, an optional space, two letters (SA, SD and SS are never issued).
        PostcodePattern = @"^[1-9][0-9]{3} ?(?!SA|SD|SS)[A-Z]{2}$",
        // BAG: huisnummer 1–99999, huisletter one letter, huisnummertoevoeging up to four characters.
        HouseNumbers = new HouseNumberRules { NumberPattern = @"^[1-9][0-9]{0,4}[A-Za-z]?$", BoxPattern = @"^[A-Za-z0-9]{1,4}$" },
        TypeaheadSteps = ["locality", "street", "houseNumber"],
        // CBS herindelingen since 1991 (PRD §4.4); woonplaatsen and streets are known since the BAG's start (2009–2011).
        HistoryKnownSince = new DateOnly(1991, 1, 1),
        TimeZone = Zone,
        // The Caribbean Netherlands are not in the EU and have their own ISO code (BQ): no territory here.
        VatTerritories = [],
    };
}
