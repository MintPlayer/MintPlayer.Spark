using System.Globalization;
using System.Text.RegularExpressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData;
using MintPlayer.Spark.ReferenceData.Build;
using MintPlayer.Spark.ReferenceData.Releases;
using MintPlayer.Spark.ReferenceData.Sources.Latvia;

namespace ReferenceData.Tests.Adapters;

/// <summary>
/// The Latvian adapter (PRD §4.4, plan R8) on its real fixtures cut from the VZD State Address Register (2026-10-19):
/// Madona, Cesvaine and Varakļāni through the 2009, 2021 and 2025 reforms, Rīga as a state city.
/// </summary>
public class LatviaAdapterTests
{
    private const string Madona2021 = "places/lv/var/100016671";
    private const string MadonaOld = "places/lv/var/100016003";
    private const string Cesvaines = "places/lv/var/100015782";
    private const string Varaklanu = "places/lv/var/100016300";
    private const string MadonasRajons = "places/lv/var/100001153";
    private const string VaraklaniTown = "places/lv/var/100003462";
    private const string MadonaTown = "places/lv/var/100003438";
    private const string Riga = "places/lv/var/100003003";

    private static Task<RefDataBuild> BuildAsync()
        => new RefDatasetBuilder([new LatviaAddressRegisterSource()], [new LatviaMapper()]).BuildFixturesAsync(CancellationToken.None);

    private static DateOnly D(string text) => DateOnly.Parse(text, CultureInfo.InvariantCulture);

    [Fact]
    public async Task The_profile_is_Latvian()
    {
        var build = await BuildAsync();
        build.Ledger.Conflicts.Should().BeEmpty();
        var country = build.Countries["countries/lv"];
        country.Alpha3.Should().Be("LVA");
        country.Place.Should().Be("places/lv/iso3166/LV");
        country.Profile.MunicipalityLevel.Should().Be("lv:municipality");
        country.Profile.TimeZone.Should().Be("Europe/Riga");
        country.Profile.HistoryKnownSince.Should().Be(D("1999-03-16"));
        Regex.IsMatch("LV-4838", country.Profile.PostcodePattern!).Should().BeTrue();
        Regex.IsMatch("4838", country.Profile.PostcodePattern!).Should().BeFalse();
        country.Profile.VatTerritories.Should().BeEmpty();
    }

    [Fact]
    public async Task The_BOM_decides_the_encoding_not_the_metadata()
    {
        // The CSVW metadata says ISO-8859-1; the files are UTF-8 with a BOM.
        var build = await BuildAsync();
        build.Places[VaraklaniTown].Names.Entries.Should().Contain(e => e.Value == "Varakļāni" && e.Tag == "lv" && e.Kind == TranslatedStringKind.Official);
        build.Places[Riga].Names.Entries.Should().Contain(e => e.Value == "Rīga");
    }

    [Fact]
    public async Task A_state_city_is_a_municipality_under_the_state()
    {
        var build = await BuildAsync();
        var riga = RefAsOf.PeriodAt(build.Places[Riga], D("2026-10-19"))!;
        riga.Kind.Should().Be("lv:municipality");
        riga.Parent.Should().Be("places/lv/iso3166/LV");
        build.Places[Riga].Codes.Should().Contain(c => c.Scheme == "atvk" && c.Value == "0001000");
    }

    [Fact]
    public async Task Varaklani_moves_from_its_district_to_its_own_municipality_to_Madona()
    {
        var build = await BuildAsync();
        var town = build.Places[VaraklaniTown];
        RefAsOf.PeriodAt(town, D("2005-01-01"))!.Parent.Should().Be(MadonasRajons);
        RefAsOf.PeriodAt(town, D("2025-06-30"))!.Parent.Should().Be(Varaklanu);
        RefAsOf.PeriodAt(town, D("2025-07-01"))!.Parent.Should().Be(Madona2021);
        RefAsOf.PeriodAt(town, D("2025-07-01"))!.Kind.Should().Be("lv:town");
        RefAsOf.SegmentAt(town, D("2026-01-01"))!.Ancestors.Should().Contain("places/lv/iso3166/LV");
    }

    [Fact]
    public async Task Varaklanu_novads_is_absorbed_by_Madona_on_2025_07_01()
    {
        var build = await BuildAsync();
        build.Places[Varaklanu].Lifetime.To.Should().Be(D("2025-07-01"));
        var absorb = build.Lineage["lineage/lv/2025-07-01/absorb/var-100016671"];
        absorb.Kind.Should().Be(LineageKind.Absorb);
        absorb.Operations.Should().Contain(o => o.Place == Varaklanu && o.Op == LineageOp.End && o.Into == Madona2021 && o.Share == SuccessionShare.Whole);
        build.Places[Varaklanu].SupersededBy.Should().Contain(e => e.Id == Madona2021 && e.Date == D("2025-07-01"));
        // As of the day before, Varakļānu novads is the answer; on the day, Madona.
        (await RefAsOf.ResolveAsync(build.Places[Varaklanu], D("2025-06-30"), id => Task.FromResult(build.Places.GetValueOrDefault(id)))).Places.Select(p => p.Id).Should().Equal(Varaklanu);
        (await RefAsOf.ResolveAsync(build.Places[Varaklanu], D("2025-07-01"), id => Task.FromResult(build.Places.GetValueOrDefault(id)))).Places.Select(p => p.Id).Should().Equal(Madona2021);
    }

    [Fact]
    public async Task The_2021_reform_merges_old_Madona_and_Cesvaine_into_the_new_Madona()
    {
        var build = await BuildAsync();
        var merge = build.Lineage["lineage/lv/2021-07-01/merge/var-100016671"];
        merge.Operations.Should().Contain(o => o.Place == Madona2021 && o.Op == LineageOp.Create);
        merge.Operations.Should().Contain(o => o.Place == MadonaOld && o.Op == LineageOp.End && o.Into == Madona2021);
        merge.Operations.Should().Contain(o => o.Place == Cesvaines && o.Op == LineageOp.End && o.Into == Madona2021);
    }

    [Fact]
    public async Task A_version_under_a_parent_that_did_not_exist_yet_lies_under_its_same_name_predecessor()
    {
        // Madona town has no history row for 2021: its STD text "Madona, Madonas nov." did not change.
        var build = await BuildAsync();
        var town = build.Places[MadonaTown];
        RefAsOf.PeriodAt(town, D("2015-01-01"))!.Parent.Should().Be(MadonaOld);
        RefAsOf.PeriodAt(town, D("2022-01-01"))!.Parent.Should().Be(Madona2021);
        // Cesvaine town has one (the text changed from Cesvaines nov.): no guess needed.
        RefAsOf.PeriodAt(build.Places["places/lv/var/100003446"], D("2015-01-01"))!.Parent.Should().Be(Cesvaines);
    }

    [Fact]
    public async Task Streets_carry_their_municipality_over_time_and_postcodes_from_the_address_file()
    {
        var build = await BuildAsync();
        var brivibas = build.Streets["streets/lv/var/100371530"];
        brivibas.Names.Entries.Should().Contain(e => e.Value == "Brīvības iela" && e.Kind == TranslatedStringKind.Official);
        RefAsOf.PeriodAt(brivibas, D("2020-01-01"))!.Municipality.Should().Be(Varaklanu);
        var current = RefAsOf.PeriodAt(brivibas, D("2026-01-01"))!;
        current.Municipality.Should().Be(Madona2021);
        current.Locality.Should().Be(VaraklaniTown);
        current.PostalCodes.Should().Equal("postalcodes/lv/LV-4838");

        var postal = build.PostalCodes["postalcodes/lv/LV-4838"];
        postal.Localities.Should().ContainSingle(l => l.Place == VaraklaniTown && l.Municipalities.Any(m => m.Id == Madona2021));

        // Rīga's streets lie in the state city itself, with no locality below it.
        var janvara = RefAsOf.PeriodAt(build.Streets["streets/lv/var/100300011"], D("2026-01-01"))!;
        janvara.Municipality.Should().Be(Riga);
        janvara.PostalCodes.Should().Equal("postalcodes/lv/LV-1050");
    }

    [Fact]
    public async Task The_credit_line_names_VZD_and_CC_BY()
    {
        var build = await BuildAsync();
        build.Sources["lv-var"].Licence.Should().Be("CC BY 4.0");
        build.Sources["lv-var"].Attribution.Should().Contain("Valsts zemes dienests");
    }

    [Fact]
    public async Task Two_builds_give_the_same_bytes()
    {
        var first = await BuildAsync();
        var second = await BuildAsync();
        first.Documents().Select(d => ReleaseCanonical.Hash(d)).Should().Equal(second.Documents().Select(d => ReleaseCanonical.Hash(d)));
    }
}
