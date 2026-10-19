using System.Globalization;
using System.Text.RegularExpressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData;
using MintPlayer.Spark.ReferenceData.Build;
using MintPlayer.Spark.ReferenceData.Releases;
using MintPlayer.Spark.ReferenceData.Sources.Austria;

namespace ReferenceData.Tests.Adapters;

/// <summary>
/// The Austrian adapter (PRD §4.4, plan R8) on its real fixtures: BEV's Adressregister tables (Stichtag 2026-10-01),
/// Statistik Austria's district list and its change file since 2002 (recode, merge, split, rename).
/// </summary>
public class AustriaAdapterTests
{
    private static Task<RefDataBuild> BuildAsync()
        => new RefDatasetBuilder([new AustriaAddressRegisterSource(), new AustriaRegionsSource(), new AustriaChangesSource()], [new AustriaMapper()])
            .BuildFixturesAsync(CancellationToken.None);

    private static DateOnly D(string text) => DateOnly.Parse(text, CultureInfo.InvariantCulture);

    private static Func<string, Task<RefPlace?>> Loader(RefDataBuild build) => id => Task.FromResult(build.Places.GetValueOrDefault(id));

    [Fact]
    public async Task The_profile_and_the_hierarchy_are_Austrian()
    {
        var build = await BuildAsync();
        var country = build.Countries["countries/at"];
        country.Alpha3.Should().Be("AUT");
        country.Profile.TimeZone.Should().Be("Europe/Vienna");
        country.Profile.HistoryKnownSince.Should().Be(D("2002-01-01"));
        Regex.IsMatch("7000", country.Profile.PostcodePattern!).Should().BeTrue();

        var eisenstadt = build.Places["places/at/gkz/10101"];
        var ancestors = RefAsOf.SegmentAt(eisenstadt, D("2026-10-01"))!.Ancestors;
        ancestors[0].Should().Be("places/at/bezirk/101");
        ancestors.Should().Contain("places/at/bundesland/1");
        build.Places["places/at/bundesland/1"].Names.Entries.Single().Value.Should().Be("Burgenland");
        build.Places["places/at/okz/00002"].Periods.Single().Parent.Should().Be(eisenstadt.Id);
    }

    [Fact]
    public async Task Recode_Ebergassing_is_rekeyed_when_Wien_Umgebung_is_dissolved_2017()
    {
        var build = await BuildAsync();
        build.Places.ContainsKey("places/at/gkz/30729").Should().BeFalse();
        var ebergassing = build.Places["places/at/gkz/32401"];
        ebergassing.Codes.Should().Contain(c => c.Scheme == "gkz" && c.Value == "32401" && c.To == D("2017-01-01"));
        ebergassing.Codes.Should().Contain(c => c.Scheme == "gkz" && c.Value == "30729" && c.From == D("2017-01-01"));
        RefAsOf.PeriodAt(ebergassing, D("2016-12-31"))!.Parent.Should().Be("places/at/bezirk/324");
        RefAsOf.PeriodAt(ebergassing, D("2017-01-01"))!.Parent.Should().Be("places/at/bezirk/307");
        build.Places["places/at/bezirk/324"].Lifetime.To.Should().Be(D("2017-01-01"));
        build.Lineage["lineage/at/2017-01-01/recode/gkz-30729"].Operations.Should().Contain(o => o.Op == LineageOp.Reparent);
        // BEV names the new code; the ledger resolves it to the same place.
        build.Streets["streets/at/skz/021769"].Periods.Single().Municipality.Should().Be(ebergassing.Id);
    }

    [Fact]
    public async Task Merge_Fuerstenfeld_2025_ends_its_predecessors()
    {
        var build = await BuildAsync();
        var fuerstenfeld = build.Places["places/at/gkz/62280"];
        fuerstenfeld.Lifetime.From.Should().Be(D("2025-01-01"));
        build.Lineage.Should().ContainKey("lineage/at/2025-01-01/merge/gkz-62280");

        // Söchau was re-keyed in 2013 (60512 → 62252): its id keeps 60512.
        var soechau = build.Places["places/at/gkz/60512"];
        soechau.SupersededBy.Should().ContainSingle(e => e.Id == fuerstenfeld.Id && e.Share == SuccessionShare.Whole && e.Date == D("2025-01-01"));
        (await RefAsOf.ResolveAsync(soechau, D("2024-12-31"), Loader(build))).Places.Should().ContainSingle(p => p.Id == soechau.Id);
        (await RefAsOf.ResolveAsync(soechau, D("2025-01-01"), Loader(build))).Places.Should().ContainSingle(p => p.Id == fuerstenfeld.Id);

        // The 2015 Fürstenfeld (62267) lived ten years.
        var between = build.Places["places/at/gkz/62267"];
        between.Lifetime.From.Should().Be(D("2015-01-01"));
        between.Lifetime.To.Should().Be(D("2025-01-01"));
    }

    [Fact]
    public async Task Split_Murfeld_2020_is_ambiguous_after_the_split()
    {
        var build = await BuildAsync();
        var murfeld = build.Places["places/at/gkz/61512"];
        murfeld.SupersededBy.Where(e => e.Date == D("2020-01-01")).Select(e => e.Id).Order(StringComparer.Ordinal)
            .Should().Equal("places/at/gkz/61060", "places/at/gkz/61061");
        murfeld.SupersededBy.All(e => e.Share == SuccessionShare.Part).Should().BeTrue();
        build.Lineage.Should().ContainKey("lineage/at/2020-01-01/split/gkz-62347");
        (await RefAsOf.ResolveAsync(murfeld, D("2020-01-01"), Loader(build))).Ambiguous.Should().BeTrue();
        // The 2020 merger into Sankt Veit names only its whole predecessor; Murfeld's part comes from the split.
        build.Places["places/at/gkz/61056"].SupersededBy.Should().ContainSingle(e => e.Id == "places/at/gkz/61060" && e.Share == SuccessionShare.Whole);
    }

    [Fact]
    public async Task Rename_Lenzing_an_der_Ager_2024_keeps_the_old_name_dated()
    {
        var build = await BuildAsync();
        var lenzing = build.Places["places/at/gkz/41713"];
        lenzing.Names.Entries.Should().Contain(e => e.Value == "Lenzing" && e.Kind == TranslatedStringKind.Historic && e.ValidTo == D("2024-11-28"));
        lenzing.Names.Entries.Should().Contain(e => e.Value == "Lenzing an der Ager" && e.Kind == TranslatedStringKind.Official && e.ValidFrom == D("2024-11-28"));
        build.Lineage.Should().ContainKey("lineage/at/2024-11-28/rename/gkz-41713");
    }

    [Fact]
    public async Task Streets_get_their_postcodes_from_the_addresses()
    {
        var build = await BuildAsync();
        // Stadtbergen in Fürstenfeld has addresses under two postcodes.
        build.Streets["streets/at/skz/088516"].Periods.Single().PostalCodes.Should().Equal("postalcodes/at/8280", "postalcodes/at/8362");
        build.Streets["streets/at/skz/021798"].Qualifier.Should().Be("Ebergassing");
        var rust = build.PostalCodes["postalcodes/at/7071"].Localities.Single();
        rust.Place.Should().Be("places/at/okz/00004");
        rust.Municipalities.Should().ContainSingle(m => m.Id == "places/at/gkz/10201");
        build.Documents().Select(d => ReleaseCanonical.Serialize(d)).Any(json => json.Contains("HAUSNR", StringComparison.Ordinal)).Should().BeFalse();
    }

    [Fact]
    public void The_record_date_is_the_source_version_the_credit_quotes()
    {
        AustriaAddressRegisterSource.StichtagOf(new AustriaAddressRegisterSource().Fixture()).Should().Be("2026-10-01");
        new AustriaAddressRegisterSource().Licence.Attribution.Should().Contain("Österreichisches Adressregister");
        new AustriaChangesSource().Licence.Attribution.Should().Contain("Q: STATISTIK AUSTRIA");
    }

    [Fact]
    public void The_change_file_is_read_as_sheets()
    {
        using var stream = new AustriaChangesSource().Fixture().Files.Single().Open();
        var sheets = OdsReader.Read(stream);
        sheets.Keys.Should().Contain("Zusammenlegungen");
        sheets.Keys.Should().Contain("Teilungen");
        sheets["Namensaenderungen"].Should().Contain(r => r.Length > 2 && r[0] == "41713" && r[2] == "Lenzing an der Ager");
    }

    [Fact]
    public async Task Two_builds_give_the_same_hashes()
    {
        var first = await BuildAsync();
        var second = await BuildAsync();
        Hashes(second).Should().Equal(Hashes(first));
    }

    private static List<string> Hashes(RefDataBuild build)
        => [.. build.Documents().Select(d => (string)d.GetType().GetProperty("Id")!.GetValue(d)! + "|" + ReleaseCanonical.Hash(d)).Order(StringComparer.Ordinal)];
}
