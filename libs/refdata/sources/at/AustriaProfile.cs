using static MintPlayer.Spark.ReferenceData.Sources.RefAdapterKit;

namespace MintPlayer.Spark.ReferenceData.Sources.Austria;

/// <summary>
/// The Austrian country profile (RD-DEF 6), hand-authored.
/// </summary>
/// <remarks>
/// <b>VAT territories.</b> Council Directive 2006/112/EC, Article 6, names no Austrian territory, as recalled (Jungholz and
/// Mittelberg are not listed). ⚠️ Not confirmed on EUR-Lex in this
/// session; verify against https://eur-lex.europa.eu/eli/dir/2006/112/oj before a host relies on it (commerce D37).
/// </remarks>
public static class AustriaProfile
{
    public const string Zone = "Europe/Vienna";

    public static CountryProfile Create() => new()
    {
        Levels =
        [
            Level("at:country", PlaceRole.Country, 0, 1, false, ("de", "Staat"), ("en", "Country"), ("fr", "Pays"), ("nl", "Land")),
            Level("at:state", PlaceRole.Region, 1, 2, false, ("de", "Bundesland"), ("en", "State"), ("fr", "Land"), ("nl", "Deelstaat")),
            Level("at:district", PlaceRole.Subregion, 2, 2, false, ("de", "Politischer Bezirk"), ("en", "District"), ("fr", "District"), ("nl", "District")),
            Level("at:municipality", PlaceRole.Municipality, 3, 7, false, ("de", "Gemeinde"), ("en", "Municipality"), ("fr", "Commune"), ("nl", "Gemeente")),
            Level("at:locality", PlaceRole.Locality, 4, 6, true, ("de", "Ortschaft"), ("en", "Locality"), ("fr", "Localité"), ("nl", "Plaats")),
        ],
        MunicipalityLevel = "at:municipality",
        OfficialLanguages = [new() { Lang = "de" }],
        // "Hauptstraße 12/3/5": number, then staircase and door after slashes.
        AddressLines = ["{street} {houseNumber}[/{box}]", "{postalCode} {locality}"],
        PostcodePattern = @"^[1-9][0-9]{3}$",
        HouseNumbers = new HouseNumberRules { NumberPattern = @"^[1-9][0-9]{0,4}[A-Za-z]?(-[1-9][0-9]{0,4}[A-Za-z]?)?$", BoxPattern = @"^[0-9A-Za-z]{1,6}(/[0-9A-Za-z]{1,6}){0,2}$" },
        TypeaheadSteps = ["locality", "street", "houseNumber"],
        // Statistik Austria's change file starts in 2002 (its PDF reaches back to 1945, not machine-readable).
        HistoryKnownSince = new DateOnly(2002, 1, 1),
        TimeZone = Zone,
        VatTerritories = [],
    };
}
