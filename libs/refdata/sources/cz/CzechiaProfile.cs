using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.ReferenceData.Sources.Czechia;

/// <summary>The Czech country profile (RD-DEF 6), hand-authored (nothing copied from libaddressinput).</summary>
public static class CzechiaProfile
{
    public const string Zone = "Europe/Prague";

    public static CountryProfile Create() => new()
    {
        Levels =
        [
            RefAdapterKit.Level("cz:country", PlaceRole.Country, 0, 1, false, ("cs", "Stát"), ("en", "Country"), ("de", "Staat"), ("fr", "Pays"), ("nl", "Land")),
            RefAdapterKit.Level("cz:region", PlaceRole.Region, 1, 2, false, ("cs", "Kraj"), ("en", "Region"), ("de", "Region"), ("fr", "Région"), ("nl", "Regio")),
            RefAdapterKit.Level("cz:district", PlaceRole.Subregion, 2, 2, true, ("cs", "Okres"), ("en", "District"), ("de", "Bezirk"), ("fr", "District"), ("nl", "District")),
            RefAdapterKit.Level("cz:municipality", PlaceRole.Municipality, 3, 7, false, ("cs", "Obec"), ("en", "Municipality"), ("de", "Gemeinde"), ("fr", "Commune"), ("nl", "Gemeente")),
            // A part of a municipality (část obce): the unit an address without a street names instead of a street.
            RefAdapterKit.Level("cz:part", PlaceRole.Submunicipality, 4, 6, true, ("cs", "Část obce"), ("en", "Part of municipality"), ("de", "Ortsteil"), ("fr", "Partie de commune"), ("nl", "Deelgemeente")),
        ],
        MunicipalityLevel = "cz:municipality",
        OfficialLanguages = [new() { Lang = "cs" }],
        // "Husova 21/13" / "460 01 Liberec"; where an obec has no street network the part's name stands in for the street.
        AddressLines = ["{street} {houseNumber}", "{postalCode} {locality}"],
        PostcodePattern = @"^[1-7][0-9]{2} ?[0-9]{2}$",
        // Číslo popisné (or "č. ev." evidenční) with an optional /číslo orientační and its letter.
        HouseNumbers = new HouseNumberRules { NumberPattern = @"^(č\. ?ev\. ?)?[1-9][0-9]{0,4}(/[1-9][0-9]{0,3}[a-zA-Z]?)?$" },
        TypeaheadSteps = ["locality", "street", "houseNumber"],
        // RÚIAN (ISÚI) has kept dated history since it went live on 2012-07-01; units carry their creation dates.
        HistoryKnownSince = new DateOnly(2012, 7, 1),
        TimeZone = Zone,
        // Czechia has no territory outside the EU VAT territory (Directive 2006/112/EC Art. 6 names none).
        VatTerritories = [],
    };
}
