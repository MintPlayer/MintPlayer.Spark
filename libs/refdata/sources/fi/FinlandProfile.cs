using MintPlayer.Spark.Abstractions;
using static MintPlayer.Spark.ReferenceData.Sources.RefAdapterKit;

namespace MintPlayer.Spark.ReferenceData.Sources.Finland;

/// <summary>
/// The Finnish country profile (RD-DEF 6), hand-authored. Statistics Finland lists the Åland municipalities among
/// Finland's (Åland also has its own ISO 3166-1 code, AX); the profile marks Åland as a VAT territory (commerce GA4).
/// </summary>
/// <remarks>
/// <b>Åland, legal basis.</b> Council Directive 2006/112/EC, Art. 6(1)(d): the Åland Islands are part of the
/// Community customs territory but excluded from the VAT territory. ⚠️ Not confirmed on EUR-Lex from here (the
/// consolidated text could not be fetched on 2026-10-19); verify at https://eur-lex.europa.eu/eli/dir/2006/112/oj
/// before a host relies on it (commerce D37). Postcodes 22100–22999 are Åland's.
/// </remarks>
public static class FinlandProfile
{
    public static CountryProfile Create() => new()
    {
        Levels =
        [
            Level("fi:country", PlaceRole.Country, 0, 1, false, ("fi", "Valtio"), ("sv", "Stat"), ("en", "Country"), ("de", "Staat"), ("fr", "Pays"), ("nl", "Land")),
            Level("fi:region", PlaceRole.Region, 1, 2, false, ("fi", "Maakunta"), ("sv", "Landskap"), ("en", "Region"), ("de", "Landschaft"), ("fr", "Région"), ("nl", "Landschap")),
            Level("fi:municipality", PlaceRole.Municipality, 2, 7, false, ("fi", "Kunta"), ("sv", "Kommun"), ("en", "Municipality"), ("de", "Gemeinde"), ("fr", "Commune"), ("nl", "Gemeente")),
        ],
        MunicipalityLevel = "fi:municipality",
        OfficialLanguages = [new() { Lang = "fi" }, new() { Lang = "sv" }],
        // "Mannerheimintie 1 A 2" (staircase and flat in the box), "00100 Helsinki".
        AddressLines = ["{street} {houseNumber}[ {box}]", "{postalCode} {locality}"],
        PostcodePattern = @"^[0-9]{5}$",
        HouseNumbers = new HouseNumberRules { NumberPattern = @"^[1-9][0-9]{0,4}[a-z]?$", BoxPattern = @"^[A-Za-z]( ?[0-9]{1,4}[a-z]?)?$" },
        TypeaheadSteps = ["locality", "street", "houseNumber"],
        // The yearly municipality lists start in 1970 (kunta_1_19700101).
        HistoryKnownSince = new DateOnly(1970, 1, 1),
        TimeZone = FinlandMapper.Zone,
        VatTerritories =
        [
            new VatTerritory
            {
                Code = "AX",
                Names = TranslatedString.Of(("sv", "Åland"), ("fi", "Ahvenanmaa"), ("en", "Åland Islands"), ("de", "Åland"), ("fr", "Åland"), ("nl", "Åland")),
                Treatment = VatTerritoryTreatment.OutsideVatInsideCustoms,
                PostcodePattern = @"^22[1-9][0-9]{2}$",
                LegalBasis = "Directive 2006/112/EC, Art. 6(1)(d)",
                Note = "Article letter not yet confirmed on EUR-Lex (fetch refused 2026-10-19); verify before relying on it.",
            },
        ],
    };
}
