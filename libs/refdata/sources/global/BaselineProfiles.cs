using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.ReferenceData.Sources.Global;

/// <summary>
/// Baseline country profiles for the seed's countries without a country adapter (plan R7 "tier-2 country profiles",
/// RD-DEF 6): generic levels (country, first level, municipality), a hand-authored address template, the postcode
/// pattern and the VAT territories (commerce GA4). Today: Germany. The Netherlands, France and Luxembourg moved to their
/// tier-1 adapters (plan R8: <c>Sources.Netherlands</c>, <c>Sources.France</c> with the French VAT territories,
/// <c>Sources.Luxembourg</c>), which replace a baseline profile wherever they are registered.
/// </summary>
/// <remarks>
/// <b>VAT territories, legal basis.</b> Council Directive 2006/112/EC, Article 6 (as amended by Directive (EU) 2019/475):
/// Art. 6(1) lists territories inside the Union's customs territory but outside its VAT territory (Mount Athos, the
/// Canary Islands, the French territories of Art. 349 and Art. 355(1) TFEU, the Åland Islands, Campione d'Italia, the
/// Italian waters of Lake Lugano); Art. 6(2) those outside both (Heligoland, Büsingen am Hochrhein, Ceuta, Melilla,
/// Livigno). ⚠️ <b>Not confirmed on EUR-Lex</b>: the consolidated text could not be fetched on 2026-10-19 (EUR-Lex
/// answered HTTP 202 with an empty body to every request). The letters below follow the amended text as recalled and
/// must be checked against https://eur-lex.europa.eu/eli/dir/2006/112/oj before a host relies on them (commerce D37).
/// The postcode patterns are the national postal codes of those territories (Germany: 27498 Helgoland, 78266 Büsingen;
/// France's are in <c>FranceProfile</c>).
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
