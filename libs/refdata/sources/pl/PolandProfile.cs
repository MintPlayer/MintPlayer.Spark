using MintPlayer.Spark.Abstractions;
using static MintPlayer.Spark.ReferenceData.Sources.RefAdapterKit;

namespace MintPlayer.Spark.ReferenceData.Sources.Poland;

/// <summary>
/// The Polish country profile (RD-DEF 6), hand-authored: voivodeship (województwo) → county (powiat; a city with county
/// rights is a county and a municipality at once) → municipality (gmina) → locality (SIMC miejscowość), with Warsaw's
/// districts (dzielnice) and the delegations of some cities under their gmina.
/// </summary>
public static class PolandProfile
{
    public const string Zone = "Europe/Warsaw";

    public static CountryProfile Create() => new()
    {
        Levels =
        [
            Level("pl:country", PlaceRole.Country, 0, 1, false, ("pl", "Państwo"), ("en", "Country"), ("de", "Staat"), ("fr", "Pays"), ("nl", "Land")),
            Level("pl:voivodeship", PlaceRole.Region, 1, 2, false, ("pl", "Województwo"), ("en", "Voivodeship"), ("de", "Woiwodschaft"), ("fr", "Voïvodie"), ("nl", "Woiwodschap")),
            Level("pl:county", PlaceRole.Subregion, 2, 3, false, ("pl", "Powiat"), ("en", "County"), ("de", "Kreis"), ("fr", "Powiat"), ("nl", "District")),
            Level("pl:municipality", PlaceRole.Municipality, 3, 7, false, ("pl", "Gmina"), ("en", "Municipality"), ("de", "Gemeinde"), ("fr", "Commune"), ("nl", "Gemeente")),
            Level("pl:district", PlaceRole.Submunicipality, 4, 6, true, ("pl", "Dzielnica"), ("en", "District"), ("de", "Stadtbezirk"), ("fr", "Arrondissement"), ("nl", "Stadsdeel")),
            Level("pl:locality", PlaceRole.Locality, 5, 6, false, ("pl", "Miejscowość"), ("en", "Locality"), ("de", "Ortschaft"), ("fr", "Localité"), ("nl", "Plaats")),
            Level("pl:locality-part", PlaceRole.Locality, 6, 4, true, ("pl", "Część miejscowości"), ("en", "Part of a locality"), ("de", "Ortsteil"), ("fr", "Partie de localité"), ("nl", "Deel van een plaats")),
        ],
        MunicipalityLevel = "pl:municipality",
        OfficialLanguages = [new() { Lang = "pl" }],
        // "ul. Antoniego Abrahama 37/2" / "81-833 Sopot".
        AddressLines = ["{street} {houseNumber}[/{box}]", "{postalCode} {locality}"],
        PostcodePattern = @"^[0-9]{2}-[0-9]{3}$",
        HouseNumbers = new HouseNumberRules { NumberPattern = @"^[1-9][0-9]{0,3}[A-Za-z]?$", BoxPattern = @"^[0-9]{1,4}[A-Za-z]?$" },
        TypeaheadSteps = ["locality", "street", "houseNumber"],
        // TERYT's change files need an account and are not used: history is known from the first import, so no date is claimed.
        HistoryKnownSince = null,
        TimeZone = Zone,
        // Poland has no territory outside the EU VAT territory (Directive 2006/112/EC Art. 6 names none).
        VatTerritories = [],
    };
}
