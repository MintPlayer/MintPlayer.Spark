using MintPlayer.Spark.Abstractions;
using static MintPlayer.Spark.ReferenceData.Sources.RefAdapterKit;

namespace MintPlayer.Spark.ReferenceData.Sources.France;

/// <summary>
/// The French country profile (RD-DEF 6, plan R8), hand-authored (libaddressinput states no licence). It replaces the
/// global baseline's hand-written FR profile, VAT territories included.
/// </summary>
/// <remarks>
/// <b>VAT territories, legal basis.</b> Council Directive 2006/112/EC, Article 6 (as amended by Directive (EU) 2019/475):
/// Art. 6(1) lists territories inside the Union's customs territory but outside its VAT territory, among them the French
/// territories of Art. 349 and Art. 355(1) TFEU (the outermost regions Guadeloupe, French Guiana, Martinique, Réunion,
/// Mayotte and Saint-Martin). Saint-Barthélemy left the EU in 2012 (an overseas country and territory, TFEU Art. 355(2)
/// and Annex II). ⚠️ <b>Not confirmed on EUR-Lex</b>: the consolidated text could not be fetched on 2026-10-19 (EUR-Lex
/// answered HTTP 202 with an empty body). The letters follow the amended text as recalled and must be checked against
/// https://eur-lex.europa.eu/eli/dir/2006/112/oj before a host relies on them (commerce D37). The postcode patterns are
/// the overseas départements' 97x codes (Saint-Barthélemy 97133 and Saint-Martin 97150 carved out of 971).
/// </remarks>
public static class FranceProfile
{
    private const string Unconfirmed = "Article letter not yet confirmed on EUR-Lex (fetch refused 2026-10-19); verify before relying on it.";

    /// <summary>The zone of metropolitan France (Corsica included).</summary>
    public const string Metropolitan = "Europe/Paris";

    /// <summary>The zone of each overseas region (COG region code), Q-R10: the region's own zone.</summary>
    public static readonly IReadOnlyDictionary<string, string> RegionZones = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["01"] = "America/Guadeloupe",
        ["02"] = "America/Martinique",
        ["03"] = "America/Cayenne",
        ["04"] = "Indian/Reunion",
        ["06"] = "Indian/Mayotte",
    };

    public static string ZoneOfRegion(string region) => RegionZones.GetValueOrDefault(region) ?? Metropolitan;

    public static CountryProfile Create() => new()
    {
        Levels =
        [
            Level("fr:country", PlaceRole.Country, 0, 1, false, ("fr", "Pays"), ("en", "Country"), ("de", "Staat"), ("nl", "Land")),
            Level("fr:region", PlaceRole.Region, 1, 2, false, ("fr", "Région"), ("en", "Region"), ("de", "Region"), ("nl", "Regio")),
            Level("fr:department", PlaceRole.Subregion, 2, 3, false, ("fr", "Département"), ("en", "Department"), ("de", "Département"), ("nl", "Departement")),
            Level("fr:commune", PlaceRole.Municipality, 3, 7, false, ("fr", "Commune"), ("en", "Commune"), ("de", "Gemeinde"), ("nl", "Gemeente")),
            Level("fr:commune-deleguee", PlaceRole.Submunicipality, 4, 6, true, ("fr", "Commune déléguée"), ("en", "Delegated commune"), ("de", "Delegierte Gemeinde"), ("nl", "Gedelegeerde gemeente")),
            Level("fr:commune-associee", PlaceRole.Submunicipality, 4, 6, true, ("fr", "Commune associée"), ("en", "Associated commune"), ("de", "Assoziierte Gemeinde"), ("nl", "Geassocieerde gemeente")),
        ],
        MunicipalityLevel = "fr:commune",
        OfficialLanguages = [new() { Lang = "fr" }],
        AddressLines = ["{houseNumber}[ {box}] {street}", "{postalCode} {locality}"],
        PostcodePattern = @"^[0-9]{5}$",
        HouseNumbers = new HouseNumberRules { NumberPattern = @"^[1-9][0-9]{0,4}$", BoxPattern = @"^(bis|ter|quater|[A-Za-z])$" },
        TypeaheadSteps = ["locality", "street", "houseNumber"],
        // INSEE's v_commune_depuis_1943 (PRD §4.4).
        HistoryKnownSince = new DateOnly(1943, 1, 1),
        // Metropolitan France and the overseas départements have different zones: places carry their own (Q-R10).
        TimeZone = null,
        VatTerritories =
        [
            Territory("FR-GP", VatTerritoryTreatment.OutsideVatInsideCustoms, @"^971(?!33|50)[0-9]{2}$", "Directive 2006/112/EC, Art. 6(1)(c); TFEU Art. 349", ("fr", "Guadeloupe"), ("en", "Guadeloupe"), ("de", "Guadeloupe"), ("nl", "Guadeloupe")),
            Territory("FR-MQ", VatTerritoryTreatment.OutsideVatInsideCustoms, @"^972[0-9]{2}$", "Directive 2006/112/EC, Art. 6(1)(c); TFEU Art. 349", ("fr", "Martinique"), ("en", "Martinique"), ("de", "Martinique"), ("nl", "Martinique")),
            Territory("FR-GF", VatTerritoryTreatment.OutsideVatInsideCustoms, @"^973[0-9]{2}$", "Directive 2006/112/EC, Art. 6(1)(c); TFEU Art. 349", ("fr", "Guyane"), ("en", "French Guiana"), ("de", "Französisch-Guayana"), ("nl", "Frans-Guyana")),
            Territory("FR-RE", VatTerritoryTreatment.OutsideVatInsideCustoms, @"^974[0-9]{2}$", "Directive 2006/112/EC, Art. 6(1)(c); TFEU Art. 349", ("fr", "La Réunion"), ("en", "Réunion"), ("de", "Réunion"), ("nl", "Réunion")),
            Territory("FR-YT", VatTerritoryTreatment.OutsideVatInsideCustoms, @"^976[0-9]{2}$", "Directive 2006/112/EC, Art. 6(1)(c); TFEU Art. 349", ("fr", "Mayotte"), ("en", "Mayotte"), ("de", "Mayotte"), ("nl", "Mayotte")),
            Territory("FR-MF", VatTerritoryTreatment.OutsideVatInsideCustoms, @"^97150$", "Directive 2006/112/EC, Art. 6(1)(c); TFEU Art. 349", ("fr", "Saint-Martin"), ("en", "Saint Martin"), ("de", "Saint-Martin"), ("nl", "Sint-Maarten (Frans deel)")),
            Territory("FR-BL", VatTerritoryTreatment.OutsideEu, @"^97133$", "TFEU Art. 355(2) and Annex II (an overseas country and territory since 2012)", ("fr", "Saint-Barthélemy"), ("en", "Saint Barthélemy"), ("de", "Saint-Barthélemy"), ("nl", "Saint-Barthélemy")),
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
