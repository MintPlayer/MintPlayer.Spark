using System.Text.RegularExpressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData.Build;
using static MintPlayer.Spark.ReferenceData.Sources.RefAdapterKit;

namespace MintPlayer.Spark.ReferenceData.Sources.Liechtenstein;

/// <summary>Credit line (PRD §4.4, §4.12): attribution is "expected" under swisstopo's OGD terms, as for Switzerland.</summary>
internal static class LiechtensteinLicences
{
    public static readonly SourceLicence Swisstopo = new(
        "©swisstopo", "OGD swisstopo (free use, attribution required)", "https://www.swisstopo.admin.ch/en/terms-of-use-free-geodata-and-geoservices",
        "©swisstopo — official building-address directory for Liechtenstein. The data was modified: streets and postal localities are derived from the addresses; no house numbers are kept.");
}

/// <summary>
/// swisstopo's official building-address directory for Liechtenstein (<c>li-swisstopo-addresses</c>, daily, 386 KB):
/// the CH schema with <c>COM_FOSNR</c> 7001–7011 and an empty <c>COM_CANTON</c>. UTF-8 with a BOM, <c>;</c>-separated.
/// Change check: the file's <c>ETag</c>.
/// </summary>
internal sealed partial class LiechtensteinAddressesSource : RefFileSource
{
    public const string SourceId = "li-swisstopo-addresses";
    public override string Id => SourceId;
    public override string Country => "li";
    public override SourceLicence Licence => LiechtensteinLicences.Swisstopo;
    public override IReadOnlyList<string> DownloadHosts => ["data.geo.admin.ch"];
    public override string DefaultSchedule => "0 45 5 * * *";
    protected override string DownloadUrl => "https://data.geo.admin.ch/ch.swisstopo.amtliches-gebaeudeadressverzeichnis/amtliches-gebaeudeadressverzeichnis_li/amtliches-gebaeudeadressverzeichnis_li_2056.csv.zip";
    protected override Regex? EntryPattern => AddressFile();
    protected override long MaxBytes => 64L * 1024 * 1024;
    protected override IReadOnlyList<string> FixtureNames => ["amtliches-gebaeudeadressverzeichnis_li_2056.cut.csv"];

    [GeneratedRegex(@"^amtliches-gebaeudeadressverzeichnis_li_\d+(\.cut)?\.csv$")]
    internal static partial Regex AddressFile();
}

/// <summary>The Liechtenstein country profile (RD-DEF 6), hand-authored. Not in the EU: no EU VAT territories.</summary>
public static class LiechtensteinProfile
{
    public const string Zone = "Europe/Vaduz";

    public static CountryProfile Create() => new()
    {
        Levels =
        [
            Level("li:country", PlaceRole.Country, 0, 1, false, ("de", "Staat"), ("en", "Country"), ("fr", "Pays"), ("nl", "Land")),
            Level("li:municipality", PlaceRole.Municipality, 1, 7, false, ("de", "Gemeinde"), ("en", "Municipality"), ("fr", "Commune"), ("nl", "Gemeente")),
        ],
        MunicipalityLevel = "li:municipality",
        OfficialLanguages = [new() { Lang = "de" }],
        AddressLines = ["{street} {houseNumber}", "{postalCode} {locality}"],
        PostcodePattern = @"^94(8[5-9]|9[0-8])$",
        HouseNumbers = new HouseNumberRules { NumberPattern = @"^[1-9][0-9]{0,3}[A-Za-z]?$" },
        TypeaheadSteps = ["locality", "street", "houseNumber"],
        // No machine-readable history is named for Liechtenstein: known since the first import (RD4).
        HistoryKnownSince = null,
        TimeZone = Zone,
        VatTerritories = [],
    };
}

/// <summary>
/// Liechtenstein (PRD §4.4, plan R8): the eleven Gemeinden (FOSNR), ESID streets and postal localities, all derived from
/// the address directory with no house numbers kept (RD5). Planned addresses (<c>ADR_STATUS</c> = <c>planned</c>) are
/// skipped. The postal label "9493 Mauren FL" carries the "FL" the Swiss directory needs to tell it from Mauren TG; the
/// locality is named "Mauren". A postal code reaching across a Gemeinde border (9487 Gamprin-Bendern inside Vaduz) links
/// its locality to both.
/// </summary>
internal sealed partial class LiechtensteinMapper : IReferenceDataMapper
{
    private const string Source = LiechtensteinAddressesSource.SourceId;

    [GeneratedRegex(@"^(?<code>[0-9]{4}) (?<name>.+?)( FL)?$")]
    private static partial Regex ZipLabel();

    public IReadOnlyList<string> Countries => ["li"];
    public IReadOnlyList<string> SourceIds => [Source];
    public int Order => 10;
    public bool IsCountryAdapter => true;

    public IEnumerable<LedgerEntry> ShippedLedger() => EmbeddedSourceFiles.Ledger(typeof(LiechtensteinMapper).Assembly);

    public Task MapAsync(RefDataBuild build, IReadOnlyDictionary<string, SourceInput> inputs, CancellationToken cancellationToken)
    {
        var country = CountryPlace(build, "li", Source, LiechtensteinProfile.Zone, "de");
        Country(build, "li", "LIE", "438", LiechtensteinProfile.Create(), Source);
        if (!inputs.TryGetValue(Source, out var input) || input.Find(LiechtensteinAddressesSource.AddressFile()) is not { } file)
            return Task.CompletedTask;

        using var stream = file.Open();
        foreach (var row in RefCsv.ReadRecords(stream, ';'))
        {
            if (row["ADR_STATUS"] != "real" || row["COM_FOSNR"].Length == 0 || row["STR_ESID"].Length == 0)
                continue;
            var municipality = build.Ledger.Mint(Source, "fosnr:" + row["COM_FOSNR"], RefIds.Place("li", "fosnr", row["COM_FOSNR"]));
            var place = build.Place(municipality);
            if (place.Periods.Count == 0)
            {
                place.Periods = [new PlacePeriod { Kind = "li:municipality", Role = PlaceRole.Municipality, Parent = country }];
                place.Names = new TranslatedString { Entries = [Name("de", row["COM_NAME"], TranslatedStringKind.Official, Source)] };
                place.OfficialLanguages = [new() { Lang = "de" }];
                place.Codes = [new PlaceCode { Scheme = "fosnr", Value = row["COM_FOSNR"] }];
                place.TimeZone = LiechtensteinProfile.Zone;
                AddSource(place.Source, Source);
            }

            var streetId = build.Ledger.Mint(Source, "esid:" + row["STR_ESID"], RefIds.Street("li", "esid", row["STR_ESID"]));
            var street = build.Street(streetId, "li");
            if (street.Names.Entries.Count == 0)
            {
                street.Names = new TranslatedString { Entries = [Name("de", row["STN_LABEL"], TranslatedStringKind.Official, Source)] };
                street.Type = StreetType.Streetname;
                street.Codes = [new PlaceCode { Scheme = "esid", Value = row["STR_ESID"] }];
                AddSource(street.Source, Source);
            }

            if (ZipLabel().Match(row["ZIP_LABEL"]) is not { Success: true } match)
            {
                build.Warnings.Add($"{Source}: address {row["ADR_EGAID"]} has an unreadable postal label '{row["ZIP_LABEL"]}'.");
                StreetIn(street, municipality, null);
                continue;
            }
            var code = match.Groups["code"].Value;
            var postalId = build.Ledger.Mint(Source, "postcode:" + code, RefIds.PostalCode("li", code));
            Locality(build.PostalCode(postalId, "li", code), match.Groups["name"].Value, "de", Source, municipality);
            StreetIn(street, municipality, postalId);
        }
        return Task.CompletedTask;
    }
}
