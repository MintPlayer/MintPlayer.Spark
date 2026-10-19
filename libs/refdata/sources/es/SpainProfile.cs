using MintPlayer.Spark.Abstractions;
using static MintPlayer.Spark.ReferenceData.Sources.RefAdapterKit;

namespace MintPlayer.Spark.ReferenceData.Sources.Spain;

/// <summary>
/// The Spanish country profile (RD-DEF 6), hand-authored. Three zones: places carry their own (Q-R10):
/// <c>Atlantic/Canary</c> in the Canary Islands, <c>Africa/Ceuta</c> in Ceuta and Melilla, <c>Europe/Madrid</c> elsewhere.
/// </summary>
/// <remarks>
/// <b>VAT territories, legal basis.</b> Council Directive 2006/112/EC, Art. 6: the Canary Islands are in the EU customs
/// territory but outside its VAT territory (Art. 6(1)); Ceuta and Melilla are outside both (Art. 6(2)). ⚠️ The article
/// letters are <b>not confirmed on EUR-Lex</b> (the consolidated text could not be fetched on 2026-10-19; Directive (EU)
/// 2019/475 renumbered the list) and must be checked at https://eur-lex.europa.eu/eli/dir/2006/112/oj before a host relies
/// on them (commerce D37). The postcode patterns are the provinces' postcode prefixes: 35 Las Palmas, 38 Santa Cruz de
/// Tenerife, 51 Ceuta, 52 Melilla.
/// </remarks>
public static class SpainProfile
{
    public const string Mainland = "Europe/Madrid";
    public const string Canaries = "Atlantic/Canary";
    public const string CeutaMelilla = "Africa/Ceuta";

    private const string Unconfirmed = "Article letter not yet confirmed on EUR-Lex (fetch refused 2026-10-19); verify before relying on it.";

    /// <summary>The zone of a municipality by its INE province code (Q-R10: the source states no zone; the province decides).</summary>
    public static string ZoneOf(string province) => province switch
    {
        "35" or "38" => Canaries,
        "51" or "52" => CeutaMelilla,
        _ => Mainland,
    };

    /// <summary>The co-official language of an autonomous community (INE CODAUTO), besides Spanish, if any.</summary>
    public static string? CoOfficialLanguage(string community) => community switch
    {
        "04" or "09" or "10" => "ca",   // Illes Balears, Cataluña, Comunitat Valenciana (Valencian is tagged ca)
        "12" => "gl",                   // Galicia
        "15" or "16" => "eu",           // Navarra (its Basque-speaking zone), País Vasco
        _ => null,
    };

    public static CountryProfile Create() => new()
    {
        Levels =
        [
            Level("es:country", PlaceRole.Country, 0, 1, false, ("es", "País"), ("en", "Country"), ("de", "Staat"), ("fr", "Pays"), ("nl", "Land")),
            Level("es:community", PlaceRole.Region, 1, 2, false, ("es", "Comunidad autónoma"), ("en", "Autonomous community"), ("de", "Autonome Gemeinschaft"), ("fr", "Communauté autonome"), ("nl", "Autonome gemeenschap")),
            Level("es:province", PlaceRole.Subregion, 2, 3, false, ("es", "Provincia"), ("en", "Province"), ("de", "Provinz"), ("fr", "Province"), ("nl", "Provincie")),
            Level("es:municipality", PlaceRole.Municipality, 3, 7, false, ("es", "Municipio"), ("en", "Municipality"), ("de", "Gemeinde"), ("fr", "Commune"), ("nl", "Gemeente")),
            Level("es:entity", PlaceRole.Locality, 4, 5, true, ("es", "Entidad de población"), ("en", "Population entity"), ("de", "Ortschaft"), ("fr", "Localité"), ("nl", "Plaats")),
        ],
        MunicipalityLevel = "es:municipality",
        OfficialLanguages = [new() { Lang = "es" }, new() { Lang = "ca" }, new() { Lang = "eu" }, new() { Lang = "gl" }],
        // "Calle Mayor, 5, 3º B" / "28013 Madrid".
        AddressLines = ["{street}, {houseNumber}[, {box}]", "{postalCode} {locality}"],
        PostcodePattern = @"^(0[1-9]|[1-4][0-9]|5[0-2])[0-9]{3}$",
        HouseNumbers = new HouseNumberRules { NumberPattern = @"^([1-9][0-9]{0,3}[A-Za-z]?|s/n|S/N)$", BoxPattern = @"^[0-9A-Za-zºª ,.\-]{1,20}$" },
        TypeaheadSteps = ["locality", "street", "houseNumber"],
        // Municipal alterations (since 1842) are not published machine-readably by URL; street periods come from the
        // Callejero's variation files from the first import on. Unknown: no date is claimed.
        HistoryKnownSince = null,
        TimeZone = null,
        VatTerritories =
        [
            Territory("ES-CN", VatTerritoryTreatment.OutsideVatInsideCustoms, @"^(35|38)[0-9]{3}$", "Directive 2006/112/EC, Art. 6(1)", ("es", "Canarias"), ("en", "Canary Islands"), ("de", "Kanarische Inseln"), ("fr", "Îles Canaries"), ("nl", "Canarische Eilanden")),
            Territory("ES-CE", VatTerritoryTreatment.OutsideVatAndCustoms, @"^51[0-9]{3}$", "Directive 2006/112/EC, Art. 6(2)", ("es", "Ceuta"), ("en", "Ceuta"), ("de", "Ceuta"), ("fr", "Ceuta"), ("nl", "Ceuta")),
            Territory("ES-ML", VatTerritoryTreatment.OutsideVatAndCustoms, @"^52[0-9]{3}$", "Directive 2006/112/EC, Art. 6(2)", ("es", "Melilla"), ("en", "Melilla"), ("de", "Melilla"), ("fr", "Melilla"), ("nl", "Melilla")),
        ],
    };

    private static VatTerritory Territory(string code, VatTerritoryTreatment treatment, string postcodes, string basis, params (string Tag, string Name)[] names) => new()
    {
        Code = code,
        Treatment = treatment,
        PostcodePattern = postcodes,
        LegalBasis = basis,
        Note = Unconfirmed,
        Names = TranslatedString.Of(names),
    };
}
