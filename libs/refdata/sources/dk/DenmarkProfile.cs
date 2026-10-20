using MintPlayer.Spark.Abstractions;
using static MintPlayer.Spark.ReferenceData.Sources.RefAdapterKit;

namespace MintPlayer.Spark.ReferenceData.Sources.Denmark;

/// <summary>
/// The Danish country profile (RD-DEF 6), hand-authored. Greenland (GL) and the Faroe Islands (FO) have their own ISO
/// codes and are outside the EU VAT territory as separate countries, so the profile lists no VAT territory (GA4).
/// </summary>
public static class DenmarkProfile
{
    public static CountryProfile Create() => new()
    {
        Levels =
        [
            Level("dk:country", PlaceRole.Country, 0, 1, false, ("da", "Land"), ("en", "Country"), ("de", "Staat"), ("fr", "Pays"), ("nl", "Land")),
            Level("dk:region", PlaceRole.Region, 1, 2, false, ("da", "Region"), ("en", "Region"), ("de", "Region"), ("fr", "Région"), ("nl", "Regio")),
            Level("dk:municipality", PlaceRole.Municipality, 2, 7, false, ("da", "Kommune"), ("en", "Municipality"), ("de", "Kommune"), ("fr", "Commune"), ("nl", "Gemeente")),
        ],
        MunicipalityLevel = "dk:municipality",
        OfficialLanguages = [new() { Lang = "da" }],
        // "Vesterbrogade 10, 2. th." (floor and side in the box), "1620 København V".
        AddressLines = ["{street} {houseNumber}[, {box}]", "{postalCode} {locality}"],
        PostcodePattern = @"^[0-9]{4}$",
        HouseNumbers = new HouseNumberRules { NumberPattern = @"^[1-9][0-9]{0,2}[A-Z]?$", BoxPattern = @"^(st|kl|[0-9]{1,2})\.?( ?(th|tv|mf|[0-9]{1,4})\.?)?$" },
        TypeaheadSteps = ["locality", "street", "houseNumber"],
        // DAGI's units date from the 2007 municipal reform (Kommunalreformen); DAR is bitemporal from its launch.
        HistoryKnownSince = new DateOnly(2007, 1, 1),
        TimeZone = DenmarkMapper.Zone,
        VatTerritories = [],
    };
}
