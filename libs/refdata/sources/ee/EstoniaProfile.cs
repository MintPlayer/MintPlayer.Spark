using MintPlayer.Spark.Abstractions;
using static MintPlayer.Spark.ReferenceData.Sources.RefAdapterKit;

namespace MintPlayer.Spark.ReferenceData.Sources.Estonia;

/// <summary>The Estonian country profile (RD-DEF 6), hand-authored. Estonia has no special VAT territory (GA4).</summary>
public static class EstoniaProfile
{
    public static CountryProfile Create() => new()
    {
        Levels =
        [
            Level("ee:country", PlaceRole.Country, 0, 1, false, ("et", "Riik"), ("en", "Country"), ("de", "Staat"), ("fr", "Pays"), ("nl", "Land")),
            Level("ee:county", PlaceRole.Region, 1, 2, false, ("et", "Maakond"), ("en", "County"), ("de", "Kreis"), ("fr", "Comté"), ("nl", "Provincie")),
            Level("ee:municipality", PlaceRole.Municipality, 2, 7, false, ("et", "Omavalitsus"), ("en", "Municipality"), ("de", "Gemeinde"), ("fr", "Commune"), ("nl", "Gemeente")),
            // Tallinn's districts (linnaosa).
            Level("ee:district", PlaceRole.Submunicipality, 3, 5, true, ("et", "Linnaosa"), ("en", "District"), ("de", "Stadtbezirk"), ("fr", "Quartier"), ("nl", "Stadsdeel")),
            // Cities without municipal status, small boroughs and villages: the city line of an address.
            Level("ee:settlement", PlaceRole.Locality, 3, 6, true, ("et", "Asustusüksus"), ("en", "Settlement"), ("de", "Siedlung"), ("fr", "Localité"), ("nl", "Plaats")),
        ],
        MunicipalityLevel = "ee:municipality",
        OfficialLanguages = [new() { Lang = "et" }],
        // "Narva mnt 5-12", "10117 Tallinn".
        AddressLines = ["{street} {houseNumber}[-{box}]", "{postalCode} {locality}"],
        PostcodePattern = @"^[0-9]{5}$",
        HouseNumbers = new HouseNumberRules { NumberPattern = @"^[1-9][0-9]{0,3}[A-Za-z]?(/[0-9]{1,3})?$", BoxPattern = @"^[0-9]{1,4}$" },
        TypeaheadSteps = ["locality", "street", "houseNumber"],
        // Known since the first import: the depth of ADS extract 9's history is unverified (RS8).
        HistoryKnownSince = null,
        TimeZone = EstoniaMapper.Zone,
        VatTerritories = [],
    };
}
