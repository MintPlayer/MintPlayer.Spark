using MintPlayer.Spark.Abstractions;
using static MintPlayer.Spark.ReferenceData.Sources.RefAdapterKit;

namespace MintPlayer.Spark.ReferenceData.Sources.Latvia;

/// <summary>
/// The Latvian country profile (RD-DEF 6), hand-authored. Since the 2021 reform (and Varakļāni joining Madona on
/// 2025-07-01) the first level under the state is the municipality (novads) or a state city (valstspilsēta, e.g.
/// Rīga); towns (pilsēta) and parishes (pagasts) lie inside a municipality; villages (ciems) inside a parish. The
/// districts (rajons) ended on 2009-07-01 and appear only in history. Statistical and planning regions are not part of
/// the address register and are not imported.
/// </summary>
public static class LatviaProfile
{
    public const string Zone = "Europe/Riga";

    public static CountryProfile Create() => new()
    {
        Levels =
        [
            Level("lv:country", PlaceRole.Country, 0, 1, false, ("lv", "Valsts"), ("en", "Country"), ("de", "Staat"), ("fr", "Pays"), ("nl", "Land")),
            // Ended 2009-07-01: history only.
            Level("lv:district", PlaceRole.Subregion, 1, 2, true, ("lv", "Rajons"), ("en", "District"), ("de", "Bezirk"), ("fr", "District"), ("nl", "District")),
            // A novads, or a state city (valstspilsēta) directly under the state.
            Level("lv:municipality", PlaceRole.Municipality, 2, 7, false, ("lv", "Novads"), ("en", "Municipality"), ("de", "Bezirk"), ("fr", "Municipalité"), ("nl", "Gemeente")),
            Level("lv:town", PlaceRole.Submunicipality, 3, 6, true, ("lv", "Pilsēta"), ("en", "Town"), ("de", "Stadt"), ("fr", "Ville"), ("nl", "Stad")),
            Level("lv:parish", PlaceRole.Submunicipality, 3, 5, true, ("lv", "Pagasts"), ("en", "Parish"), ("de", "Gemeinde"), ("fr", "Paroisse"), ("nl", "Parochie")),
            Level("lv:village", PlaceRole.Locality, 4, 5, true, ("lv", "Ciems"), ("en", "Village"), ("de", "Dorf"), ("fr", "Village"), ("nl", "Dorp")),
        ],
        MunicipalityLevel = "lv:municipality",
        OfficialLanguages = [new() { Lang = "lv" }],
        // "Brīvības iela 1, Varakļāni, Madonas nov., LV-4838": the register's own STD form, on three lines.
        AddressLines = ["{street} {houseNumber}[-{box}]", "{locality}", "{postalCode}"],
        PostcodePattern = @"^LV-[0-9]{4}$",
        HouseNumbers = new HouseNumberRules { NumberPattern = @"^[1-9][0-9]{0,3}[A-Za-z]?(/[0-9]{1,3})?$", BoxPattern = @"^[0-9]{1,4}[A-Za-z]?$" },
        TypeaheadSteps = ["locality", "street", "houseNumber"],
        // The register's history tables start with its creation (1999-03-16); nothing earlier is machine-readable.
        HistoryKnownSince = new DateOnly(1999, 3, 16),
        TimeZone = Zone,
        // Latvia has no territory outside the EU VAT territory (Directive 2006/112/EC Art. 6 names none).
        VatTerritories = [],
    };
}
