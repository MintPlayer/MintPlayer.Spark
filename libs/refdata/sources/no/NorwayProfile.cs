using MintPlayer.Spark.Abstractions;
using static MintPlayer.Spark.ReferenceData.Sources.RefAdapterKit;

namespace MintPlayer.Spark.ReferenceData.Sources.Norway;

/// <summary>
/// The Norwegian country profile (RD-DEF 6), hand-authored. Svalbard and Jan Mayen (SJ) have their own ISO code and are
/// not part of it. Norway is outside the EU, so it has no VAT territories in the commerce sense (GA4).
/// </summary>
public static class NorwayProfile
{
    public static CountryProfile Create() => new()
    {
        Levels =
        [
            Level("no:country", PlaceRole.Country, 0, 1, false, ("no", "Land"), ("en", "Country"), ("de", "Staat"), ("fr", "Pays"), ("nl", "Land")),
            Level("no:county", PlaceRole.Region, 1, 2, false, ("no", "Fylke"), ("en", "County"), ("de", "Provinz"), ("fr", "Comté"), ("nl", "Provincie")),
            Level("no:municipality", PlaceRole.Municipality, 2, 7, false, ("no", "Kommune"), ("en", "Municipality"), ("de", "Kommune"), ("fr", "Commune"), ("nl", "Gemeente")),
        ],
        MunicipalityLevel = "no:municipality",
        OfficialLanguages = [new() { Lang = "no" }],
        AddressLines = ["{street} {houseNumber}", "{postalCode} {locality}"],
        PostcodePattern = @"^[0-9]{4}$",
        // The number with its optional letter (bokstav): "Storgata 12B". No box.
        HouseNumbers = new HouseNumberRules { NumberPattern = @"^[1-9][0-9]{0,4}[A-Za-z]?$" },
        TypeaheadSteps = ["locality", "street", "houseNumber"],
        // The import asks KLASS for every version since 1970 (NorwayKlassSource.RequestedFrom).
        HistoryKnownSince = new DateOnly(1970, 1, 1),
        TimeZone = NorwayMapper.Zone,
        VatTerritories = [],
    };
}
