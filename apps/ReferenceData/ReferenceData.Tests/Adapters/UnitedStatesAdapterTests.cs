using System.Globalization;
using System.Text.RegularExpressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData;
using MintPlayer.Spark.ReferenceData.Build;
using MintPlayer.Spark.ReferenceData.Releases;
using MintPlayer.Spark.ReferenceData.Sources;
using MintPlayer.Spark.ReferenceData.Sources.UnitedStates;

namespace ReferenceData.Tests.Adapters;

/// <summary>
/// The U.S. adapter (PRD §4.4, plan R8) on real Census files (2026-10-19): state and county codes, the 2010s and 2020s
/// county-change pages (absorb, split, recode, a reissued GEOID) and Loving County TX's address-range table.
/// </summary>
public class UnitedStatesAdapterTests
{
    private static Task<RefDataBuild> BuildAsync()
        => new RefDatasetBuilder(
                [new UnitedStatesCodesSource(), new UnitedStatesCountyChangesSource(), new UnitedStatesAddressRangesSource()],
                [new UnitedStatesMapper()])
            .BuildFixturesAsync(CancellationToken.None);

    private static DateOnly D(string text) => DateOnly.Parse(text, CultureInfo.InvariantCulture);

    private static Func<string, Task<RefPlace?>> Loader(RefDataBuild build) => id => Task.FromResult(build.Places.GetValueOrDefault(id));

    [Fact]
    public async Task The_profile_and_the_states_are_American()
    {
        var build = await BuildAsync();
        build.Ledger.Conflicts.Should().BeEmpty();
        var country = build.Countries["countries/us"];
        country.Alpha3.Should().Be("USA");
        country.Profile.TimeZone.Should().BeNull();
        country.Profile.HistoryKnownSince.Should().Be(D("1970-01-01"));
        Regex.IsMatch("79754-1234", country.Profile.PostcodePattern!).Should().BeTrue();
        var texas = build.Places["places/us/geoid/48"];
        texas.Names.Entries.Should().ContainSingle(e => e.Value == "Texas");
        texas.Codes.Should().Contain(c => c.Scheme == "usps" && c.Value == "TX");
        RefAsOf.PeriodAt(texas, D("2026-01-01"))!.Parent.Should().Be("places/us/iso3166/US");
    }

    [Fact]
    public async Task A_county_gets_its_states_zone_only_when_the_state_has_one()
    {
        var build = await BuildAsync();
        build.Places["places/us/geoid/51019"].TimeZone.Should().Be("America/New_York");
        var loving = build.Places["places/us/geoid/48301"];
        loving.TimeZone.Should().BeNull();
        loving.TimeZoneNote.Should().Be(UnitedStatesProfile.SplitStateNote);
    }

    [Fact]
    public async Task Address_ranges_give_streets_and_non_authoritative_zip_codes()
    {
        var build = await BuildAsync();
        var dallas = build.Streets["streets/us/linearid/110764077779"];
        dallas.Names.Entries.Should().ContainSingle(e => e.Tag == "en" && e.Value == "Dallas St");
        var period = RefAsOf.PeriodAt(dallas, D("2026-10-01"))!;
        period.Municipality.Should().Be("places/us/geoid/48301");
        period.PostalCodes.Should().Equal("postalcodes/us/79754");
        build.PostalCodes.Keys.Should().Equal("postalcodes/us/79754");
        build.Sources["us-addrfeat"].Attribution.Should().Contain("NOT authoritative USPS data");
        build.Sources["us-addrfeat"].Attribution.Should().NotContain("TIGER");
    }

    [Fact]
    public async Task An_absorbed_independent_city_resolves_to_its_county()
    {
        var build = await BuildAsync();
        var city = build.Places["places/us/geoid/51515"];
        city.Lifetime.To.Should().Be(D("2013-07-01"));
        city.SupersededBy.Should().ContainSingle(e => e.Id == "places/us/geoid/51019" && e.Share == SuccessionShare.Whole);
        (await RefAsOf.ResolveAsync(city, D("2013-06-30"), Loader(build))).Places.Select(p => p.Id).Should().Equal("places/us/geoid/51515");
        (await RefAsOf.ResolveAsync(city, D("2013-07-01"), Loader(build))).Places.Select(p => p.Id).Should().Equal("places/us/geoid/51019");
    }

    [Fact]
    public async Task A_split_census_area_resolves_ambiguously_to_both_parts()
    {
        var build = await BuildAsync();
        var valdez = build.Places["places/us/geoid/02261"];
        var result = await RefAsOf.ResolveAsync(valdez, D("2019-01-02"), Loader(build));
        result.Places.Select(p => p.Id).Should().Equal("places/us/geoid/02063", "places/us/geoid/02066");
        result.Ambiguous.Should().BeTrue();
        build.Places["places/us/geoid/02063"].Lifetime.From.Should().Be(D("2019-01-02"));
    }

    [Fact]
    public async Task A_recode_keeps_the_id_the_old_code_minted()
    {
        var build = await BuildAsync();
        build.Places.ContainsKey("places/us/geoid/02158").Should().BeFalse();
        build.Ledger.Find("geoid:02158").Should().Be("places/us/geoid/02270");
        var kusilvak = build.Places["places/us/geoid/02270"];
        kusilvak.Codes.Should().Contain(c => c.Value == "02270" && c.To == D("2015-07-01"));
        kusilvak.Codes.Should().Contain(c => c.Value == "02158" && c.From == D("2015-07-01"));
        kusilvak.Names.Entries.Should().Contain(e => e.Value == "Wade Hampton Census Area" && e.Kind == TranslatedStringKind.Historic && e.ValidTo == D("2015-07-01"));
        kusilvak.Names.Entries.Should().Contain(e => e.Value == "Kusilvak Census Area" && e.ValidTo == null);
        build.Lineage.Should().ContainKey("lineage/us/2015-07-01/recode/geoid-02158");
    }

    [Fact]
    public async Task A_reissued_geoid_gets_a_dated_id()
    {
        var build = await BuildAsync();
        var area = build.Places["places/us/geoid/02195"];
        area.Lifetime.To.Should().Be(D("2013-01-03"));
        area.Names.Entries.Should().Contain(e => e.Value == "Petersburg Census Area");
        var borough = build.Places["places/us/geoid/02195@2013-01-03"];
        borough.Lifetime.From.Should().Be(D("2013-01-03"));
        borough.Names.Entries.Should().Contain(e => e.Value == "Petersburg Borough");
        // Hoonah-Angoon ceded a part to the new borough and continues.
        build.Places["places/us/geoid/02105"].SupersededBy.Should().Contain(e => e.Id == "places/us/geoid/02195@2013-01-03" && e.Share == SuccessionShare.Part);
        (build.Places["places/us/geoid/02105"].Lifetime.To is null).Should().BeTrue();
    }

    [Fact]
    public async Task Changes_of_states_the_code_lists_lack_are_skipped()
    {
        var build = await BuildAsync();
        build.Places.Keys.Should().NotContain(k => k.StartsWith("places/us/geoid/46", StringComparison.Ordinal) || k.StartsWith("places/us/geoid/22", StringComparison.Ordinal));
    }

    [Fact]
    public void The_dbase_reader_reads_every_record_of_the_attribute_table()
    {
        var file = new UnitedStatesAddressRangesSource().Fixture().Files.Single();
        using var stream = file.Open();
        var rows = DbfReader.Read(stream).ToList();
        rows.Should().HaveCount(19);
        rows[0]["FULLNAME"].Should().Be("Dallas St");
        rows[0]["ZIPL"].Should().Be("79754");
    }

    [Fact]
    public void The_page_text_extraction_keeps_the_entries_only()
    {
        var text = UnitedStatesCountyChangesSource.ExtractText(
            "<html><script>x()</script><p>Intro</p><h3>New Counties or County Equivalent Entities</h3><p>A&amp;B County, Texas (48-999): …</p><p>Page Last Revised - 2021</p></html>");
        text.Should().Be("New Counties or County Equivalent Entities A&B County, Texas (48-999): …");
    }

    [Fact]
    public async Task Two_builds_give_the_same_hashes()
    {
        var first = await BuildAsync();
        var second = await BuildAsync();
        first.Documents().Select(ReleaseCanonical.Hash).Should().Equal(second.Documents().Select(ReleaseCanonical.Hash));
    }
}
