using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.ReferenceData.Sources.Slovenia;

/// <summary>The Slovenian country profile (RD-DEF 6), hand-authored (nothing copied from libaddressinput).</summary>
public static class SloveniaProfile
{
    public const string Zone = "Europe/Ljubljana";

    /// <summary>
    /// The second official language of the bilingual municipalities (Constitution Art. 11; the self-governing national
    /// communities): Italian on the coast, Hungarian in Prekmurje. Keyed by the municipality code (občina <c>SIFRA</c>).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> BilingualMunicipalities = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["40"] = "it",  // Izola
        ["50"] = "it",  // Koper
        ["90"] = "it",  // Piran
        ["213"] = "it", // Ankaran
        ["59"] = "hu",  // Lendava
        ["156"] = "hu", // Dobrovnik
        ["158"] = "hu", // Hodoš
    };

    public static CountryProfile Create() => new()
    {
        Levels =
        [
            RefAdapterKit.Level("si:country", PlaceRole.Country, 0, 1, false, ("sl", "Država"), ("en", "Country"), ("it", "Stato"), ("hu", "Ország"), ("de", "Staat"), ("fr", "Pays"), ("nl", "Land")),
            // Statistical regions: Slovenia has no regional self-government, so the level is optional.
            RefAdapterKit.Level("si:region", PlaceRole.Region, 1, 2, true, ("sl", "Statistična regija"), ("en", "Statistical region"), ("it", "Regione statistica"), ("hu", "Statisztikai régió"), ("de", "Statistische Region"), ("fr", "Région statistique"), ("nl", "Statistische regio")),
            RefAdapterKit.Level("si:municipality", PlaceRole.Municipality, 2, 6, false, ("sl", "Občina"), ("en", "Municipality"), ("it", "Comune"), ("hu", "Község"), ("de", "Gemeinde"), ("fr", "Commune"), ("nl", "Gemeente")),
            RefAdapterKit.Level("si:settlement", PlaceRole.Locality, 3, 7, false, ("sl", "Naselje"), ("en", "Settlement"), ("it", "Località"), ("hu", "Település"), ("de", "Ortschaft"), ("fr", "Localité"), ("nl", "Plaats")),
        ],
        MunicipalityLevel = "si:municipality",
        OfficialLanguages = [new() { Lang = "sl" }],
        // "Adamičeva ulica 3" / "6330 Piran"; a settlement without a street system writes its own name instead of a street.
        AddressLines = ["{street} {houseNumber}", "{postalCode} {locality}"],
        PostcodePattern = @"^(SI-)?[1-9][0-9]{3}$",
        HouseNumbers = new HouseNumberRules { NumberPattern = @"^[1-9][0-9]{0,3}[a-zA-Z]?$" },
        TypeaheadSteps = ["locality", "street", "houseNumber"],
        // The renewed registers (March 2024) are read as current state; history is known from the first import.
        HistoryKnownSince = null,
        TimeZone = Zone,
        // Slovenia has no territory outside the EU VAT territory (Directive 2006/112/EC Art. 6 names none).
        VatTerritories = [],
    };
}
