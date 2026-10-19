using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.ReferenceData.Sources.Global;

/// <summary>
/// Baseline country profiles for the seed's other countries (plan R7 "tier-2 country profiles", RD-DEF 6): generic
/// levels (country, first level, municipality), a hand-authored address template, the postcode pattern and the VAT
/// territories (commerce GA4). A tier-1 adapter for the country (plan R8) replaces them.
/// </summary>
/// <remarks>
/// <b>VAT territories, legal basis.</b> Council Directive 2006/112/EC, Article 6 (as amended by Directive (EU) 2019/475):
/// Art. 6(1) lists territories inside the Union's customs territory but outside its VAT territory (Mount Athos, the
/// Canary Islands, the French territories of Art. 349 and Art. 355(1) TFEU, the Åland Islands, Campione d'Italia, the
/// Italian waters of Lake Lugano); Art. 6(2) those outside both (Heligoland, Büsingen am Hochrhein, Ceuta, Melilla,
/// Livigno). ⚠️ <b>Not confirmed on EUR-Lex</b>: the consolidated text could not be fetched on 2026-10-19 (EUR-Lex
/// answered HTTP 202 with an empty body to every request). The letters below follow the amended text as recalled and
/// must be checked against https://eur-lex.europa.eu/eli/dir/2006/112/oj before a host relies on them (commerce D37).
/// The postcode patterns are the national postal codes of those territories (France: the overseas departments' 97x
/// codes; Germany: 27498 Helgoland, 78266 Büsingen).
/// </remarks>
public static class BaselineProfiles
{
    private const string Unconfirmed = "Article letter not yet confirmed on EUR-Lex (fetch refused 2026-10-19); verify before relying on it.";

    public static IReadOnlyList<BaselineProfile> All() =>
    [
        new("de", "DEU", "276", new CountryProfile
        {
            Levels =
            [
                Level("de:country", PlaceRole.Country, 0, 1, ("de", "Staat"), ("en", "Country"), ("fr", "Pays"), ("nl", "Land")),
                Level("de:state", PlaceRole.Region, 1, 2, ("de", "Land"), ("en", "State"), ("fr", "Land"), ("nl", "Deelstaat")),
                Level("de:district", PlaceRole.Subregion, 2, 2, ("de", "Kreis"), ("en", "District"), ("fr", "Arrondissement"), ("nl", "Kreis")),
                Level("de:municipality", PlaceRole.Municipality, 3, 7, ("de", "Gemeinde"), ("en", "Municipality"), ("fr", "Commune"), ("nl", "Gemeente")),
            ],
            MunicipalityLevel = "de:municipality",
            OfficialLanguages = [new() { Lang = "de" }],
            AddressLines = ["{street} {houseNumber}", "{postalCode} {locality}"],
            PostcodePattern = @"^[0-9]{5}$",
            HouseNumbers = new HouseNumberRules { NumberPattern = @"^[1-9][0-9]{0,4}[A-Za-z]?$" },
            TypeaheadSteps = ["locality", "street", "houseNumber"],
            // Two zones (Europe/Berlin; Europe/Busingen for Büsingen am Hochrhein): places carry their own (Q-R10).
            TimeZone = null,
            VatTerritories =
            [
                Territory("DE-HELGOLAND", VatTerritoryTreatment.OutsideVatAndCustoms, @"^27498$", "Directive 2006/112/EC, Art. 6(2)(a)", ("de", "Helgoland"), ("en", "Heligoland"), ("fr", "Heligoland"), ("nl", "Helgoland")),
                Territory("DE-BUSINGEN", VatTerritoryTreatment.OutsideVatAndCustoms, @"^78266$", "Directive 2006/112/EC, Art. 6(2)(b)", ("de", "Büsingen am Hochrhein"), ("en", "Büsingen am Hochrhein"), ("fr", "Büsingen am Hochrhein"), ("nl", "Büsingen am Hochrhein")),
            ],
        }),
        new("fr", "FRA", "250", new CountryProfile
        {
            Levels =
            [
                Level("fr:country", PlaceRole.Country, 0, 1, ("fr", "Pays"), ("en", "Country"), ("de", "Staat"), ("nl", "Land")),
                Level("fr:region", PlaceRole.Region, 1, 2, ("fr", "Région"), ("en", "Region"), ("de", "Region"), ("nl", "Regio")),
                Level("fr:department", PlaceRole.Subregion, 2, 3, ("fr", "Département"), ("en", "Department"), ("de", "Département"), ("nl", "Departement")),
                Level("fr:commune", PlaceRole.Municipality, 3, 7, ("fr", "Commune"), ("en", "Commune"), ("de", "Gemeinde"), ("nl", "Gemeente")),
                Level("fr:commune-deleguee", PlaceRole.Submunicipality, 4, 6, ("fr", "Commune déléguée"), ("en", "Delegated commune"), ("de", "Delegierte Gemeinde"), ("nl", "Gedelegeerde gemeente")),
            ],
            MunicipalityLevel = "fr:commune",
            OfficialLanguages = [new() { Lang = "fr" }],
            AddressLines = ["{houseNumber}[ {box}] {street}", "{postalCode} {locality}"],
            PostcodePattern = @"^[0-9]{5}$",
            HouseNumbers = new HouseNumberRules { NumberPattern = @"^[1-9][0-9]{0,4}$", BoxPattern = @"^(bis|ter|quater|[A-Za-z])$" },
            TypeaheadSteps = ["locality", "street", "houseNumber"],
            // Metropolitan France and the overseas departments have different zones: places carry their own (Q-R10).
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
        }),
        new("lu", "LUX", "442", new CountryProfile
        {
            Levels =
            [
                Level("lu:country", PlaceRole.Country, 0, 1, ("fr", "Pays"), ("de", "Staat"), ("en", "Country"), ("nl", "Land")),
                Level("lu:canton", PlaceRole.Region, 1, 2, ("fr", "Canton"), ("de", "Kanton"), ("en", "Canton"), ("nl", "Kanton")),
                Level("lu:commune", PlaceRole.Municipality, 2, 7, ("fr", "Commune"), ("de", "Gemeinde"), ("en", "Municipality"), ("nl", "Gemeente")),
                Level("lu:locality", PlaceRole.Locality, 3, 5, ("fr", "Localité"), ("de", "Ortschaft"), ("en", "Locality"), ("nl", "Plaats")),
            ],
            MunicipalityLevel = "lu:commune",
            OfficialLanguages = [new() { Lang = "fr" }, new() { Lang = "de" }, new() { Lang = "lb" }],
            AddressLines = ["{houseNumber}[ {box}], {street}", "L-{postalCode} {locality}"],
            PostcodePattern = @"^(L-)?[0-9]{4}$",
            HouseNumbers = new HouseNumberRules { NumberPattern = @"^[1-9][0-9]{0,4}[A-Za-z]?$" },
            TypeaheadSteps = ["locality", "street", "houseNumber"],
            TimeZone = "Europe/Luxembourg",
            VatTerritories = [],
        }),
        new("nl", "NLD", "528", new CountryProfile
        {
            Levels =
            [
                Level("nl:country", PlaceRole.Country, 0, 1, ("nl", "Land"), ("en", "Country"), ("fr", "Pays"), ("de", "Staat")),
                Level("nl:province", PlaceRole.Region, 1, 2, ("nl", "Provincie"), ("en", "Province"), ("fr", "Province"), ("de", "Provinz")),
                Level("nl:municipality", PlaceRole.Municipality, 2, 7, ("nl", "Gemeente"), ("en", "Municipality"), ("fr", "Commune"), ("de", "Gemeinde")),
                Level("nl:woonplaats", PlaceRole.Locality, 3, 6, ("nl", "Woonplaats"), ("en", "Locality"), ("fr", "Localité"), ("de", "Ortschaft")),
            ],
            MunicipalityLevel = "nl:municipality",
            OfficialLanguages = [new() { Lang = "nl" }],
            AddressLines = ["{street} {houseNumber}[{box}]", "{postalCode} {locality}"],
            PostcodePattern = @"^[1-9][0-9]{3} ?[A-Z]{2}$",
            HouseNumbers = new HouseNumberRules { NumberPattern = @"^[1-9][0-9]{0,4}$", BoxPattern = @"^[A-Za-z0-9-]{1,4}$" },
            TypeaheadSteps = ["locality", "street", "houseNumber"],
            TimeZone = "Europe/Amsterdam",
            // The Caribbean Netherlands are not part of the EU and have their own ISO codes (BQ): none here.
            VatTerritories = [],
        }),
    ];

    private static AdminLevel Level(string key, PlaceRole role, int order, int weight, params (string Tag, string Label)[] labels) => new()
    {
        Key = key,
        Role = role,
        Order = order,
        RankWeight = weight,
        Optional = false,
        Labels = TranslatedString.Of(labels),
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

/// <param name="Code">Lower-case ISO 3166-1 alpha-2.</param>
public sealed record BaselineProfile(string Code, string Alpha3, string Numeric, CountryProfile Profile);
