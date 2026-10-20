using MintPlayer.Spark.Abstractions;
using static MintPlayer.Spark.ReferenceData.Sources.RefAdapterKit;

namespace MintPlayer.Spark.ReferenceData.Sources.Luxembourg;

/// <summary>
/// The Luxembourg country profile (RD-DEF 6, plan R8), hand-authored (libaddressinput states no licence). It replaces the
/// global baseline's hand-written LU profile.
/// </summary>
public static class LuxembourgProfile
{
    public const string Zone = "Europe/Luxembourg";

    public static CountryProfile Create() => new()
    {
        Levels =
        [
            Level("lu:country", PlaceRole.Country, 0, 1, false, ("fr", "Pays"), ("de", "Staat"), ("lb", "Land"), ("en", "Country"), ("nl", "Land")),
            Level("lu:canton", PlaceRole.Region, 1, 2, false, ("fr", "Canton"), ("de", "Kanton"), ("lb", "Kanton"), ("en", "Canton"), ("nl", "Kanton")),
            Level("lu:commune", PlaceRole.Municipality, 2, 7, false, ("fr", "Commune"), ("de", "Gemeinde"), ("lb", "Gemeng"), ("en", "Municipality"), ("nl", "Gemeente")),
            Level("lu:locality", PlaceRole.Locality, 3, 6, false, ("fr", "Localité"), ("de", "Ortschaft"), ("lb", "Uertschaft"), ("en", "Locality"), ("nl", "Plaats")),
        ],
        MunicipalityLevel = "lu:commune",
        OfficialLanguages = [new() { Lang = "fr" }, new() { Lang = "de" }, new() { Lang = "lb" }],
        AddressLines = ["{houseNumber}[ {box}], {street}", "L-{postalCode} {locality}"],
        PostcodePattern = @"^(L-)?[0-9]{4}$",
        HouseNumbers = new HouseNumberRules { NumberPattern = @"^[1-9][0-9]{0,4}[A-Za-z]?$" },
        TypeaheadSteps = ["locality", "street", "houseNumber"],
        // CACLR publishes no machine-readable history (RD4): history is known from the first import on, so the profile
        // states no date; the API then reports any asOf as before the known history.
        HistoryKnownSince = null,
        TimeZone = Zone,
        // Luxembourg has no territory outside the EU VAT territory (Directive 2006/112/EC Art. 6 names none).
        VatTerritories = [],
    };
}
