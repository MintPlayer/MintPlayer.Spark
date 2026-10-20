using System.Globalization;
using System.Text.RegularExpressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData;
using MintPlayer.Spark.ReferenceData.Build;
using MintPlayer.Spark.ReferenceData.Releases;
using MintPlayer.Spark.ReferenceData.Sources.Netherlands;

namespace ReferenceData.Tests.Adapters;

/// <summary>
/// The Dutch adapter (PRD §4.4, plan R8) on its real fixtures: CBS gemeenten (Eemsdelta, Groningen), the herindelingen of
/// 2019 (Groningen absorbs Haren and Ten Boer) and 2021 (Eemsdelta = Appingedam + Delfzijl + Loppersum), and an LVBAG
/// extract cut (woonplaatsen, Kadaster's gemeente–woonplaats relations, roads and nummeraanduidingen).
/// </summary>
public class NetherlandsAdapterTests
{
    private static Task<RefDataBuild> BuildAsync()
        => new RefDatasetBuilder([new NetherlandsCbsGemeentenSource(), new NetherlandsHerindelingenSource(), new NetherlandsBagSource()], [new NetherlandsMapper()])
            .BuildFixturesAsync(CancellationToken.None);

    private static DateOnly D(string text) => DateOnly.Parse(text, CultureInfo.InvariantCulture);

    private static Func<string, Task<RefPlace?>> Loader(RefDataBuild build) => id => Task.FromResult(build.Places.GetValueOrDefault(id));

    [Fact]
    public async Task The_profile_and_the_country_place_are_Dutch()
    {
        var build = await BuildAsync();
        build.Ledger.Conflicts.Should().BeEmpty();
        var country = build.Countries["countries/nl"];
        country.Alpha3.Should().Be("NLD");
        country.Profile.TimeZone.Should().Be("Europe/Amsterdam");
        country.Profile.MunicipalityLevel.Should().Be("nl:municipality");
        country.Profile.HistoryKnownSince.Should().Be(D("1991-01-01"));
        Regex.IsMatch("9902 HA", country.Profile.PostcodePattern!).Should().BeTrue();
        Regex.IsMatch("9902HA", country.Profile.PostcodePattern!).Should().BeTrue();
        Regex.IsMatch("9902SS", country.Profile.PostcodePattern!).Should().BeFalse();
        country.Place.Should().Be("places/nl/iso3166/NL");
    }

    [Fact]
    public async Task CBS_codes_are_trimmed_and_gemeenten_hang_under_their_province()
    {
        var build = await BuildAsync();
        var eemsdelta = build.Places["places/nl/cbs/GM1979"];
        eemsdelta.Names.Resolve(["nl"]).Should().Be("Eemsdelta");
        RefAsOf.PeriodAt(eemsdelta, D("2026-01-01"))!.Parent.Should().Be("places/nl/cbs/PV20");
        build.Places["places/nl/cbs/PV20"].Names.Resolve(["nl"]).Should().Be("Groningen");
        RefAsOf.SegmentAt(eemsdelta, D("2026-01-01"))!.Ancestors.Should().Equal("places/nl/cbs/PV20", "places/nl/iso3166/NL");
    }

    [Fact]
    public async Task A_herindeling_under_a_new_code_is_a_merge()
    {
        var build = await BuildAsync();
        var eemsdelta = build.Places["places/nl/cbs/GM1979"];
        eemsdelta.Lifetime.From.Should().Be(D("2021-01-01"));
        var merge = build.Lineage["lineage/nl/2021-01-01/merge/cbs-GM1979"];
        merge.Operations.Should().Contain(o => o.Place == "places/nl/cbs/GM1979" && o.Op == LineageOp.Create);
        merge.Operations.Count(o => o.Op == LineageOp.End && o.Into == "places/nl/cbs/GM1979").Should().Be(3);

        var appingedam = build.Places["places/nl/cbs/GM0003"];
        appingedam.Lifetime.To.Should().Be(D("2021-01-01"));
        appingedam.SupersededBy.Should().ContainSingle(e => e.Id == "places/nl/cbs/GM1979" && e.Share == SuccessionShare.Whole);
        // The ended gemeente's province comes from the table's "Gr." abbreviation.
        RefAsOf.PeriodAt(appingedam, D("2020-06-01"))!.Parent.Should().Be("places/nl/cbs/PV20");

        var before = await RefAsOf.ResolveAsync(appingedam, D("2020-12-31"), Loader(build));
        before.Places.Single().Id.Should().Be("places/nl/cbs/GM0003");
        var on = await RefAsOf.ResolveAsync(appingedam, D("2021-01-01"), Loader(build));
        on.Places.Single().Id.Should().Be("places/nl/cbs/GM1979");
    }

    [Fact]
    public async Task A_herindeling_where_a_participant_keeps_its_code_is_an_absorb()
    {
        var build = await BuildAsync();
        var absorb = build.Lineage["lineage/nl/2019-01-01/absorb/cbs-GM0014"];
        absorb.Operations.Should().Contain(o => o.Place == "places/nl/cbs/GM0014" && o.Op == LineageOp.Continue);
        absorb.Operations.Where(o => o.Op == LineageOp.End).Select(o => o.Place).Should().Equal("places/nl/cbs/GM0009", "places/nl/cbs/GM0017");
        (build.Places["places/nl/cbs/GM0014"].Lifetime.From is null).Should().BeTrue();
        build.Places["places/nl/cbs/GM0017"].Names.Entries.Should().ContainSingle(e => e.Value == "Haren" && e.ValidTo == D("2019-01-01"));
    }

    [Fact]
    public async Task A_woonplaats_follows_its_gemeente_through_the_merger()
    {
        var build = await BuildAsync();
        var appingedam = build.Places["places/nl/bag-wpl/3386"];
        // The 1-day registration sliver of 2010-10-20 is joined with the next relation.
        appingedam.Periods.Select(p => p.Parent).Should().Equal("places/nl/cbs/GM0003", "places/nl/cbs/GM1979");
        appingedam.Lifetime.From.Should().Be(D("2010-10-20"));
        RefAsOf.PeriodAt(appingedam, D("2020-06-01"))!.Parent.Should().Be("places/nl/cbs/GM0003");
        RefAsOf.PeriodAt(appingedam, D("2022-06-01"))!.Parent.Should().Be("places/nl/cbs/GM1979");
        RefAsOf.SegmentAt(appingedam, D("2022-06-01"))!.Ancestors.Should().Equal("places/nl/cbs/GM1979", "places/nl/cbs/PV20", "places/nl/iso3166/NL");
    }

    [Fact]
    public async Task Woonplaats_names_are_dated_per_voorkomen()
    {
        var build = await BuildAsync();
        var spijk = build.Places["places/nl/bag-wpl/3241"];
        spijk.Names.Entries.Should().Contain(e => e.Value == "Spijk Gn" && e.ValidFrom == D("2011-06-28") && e.ValidTo == D("2017-08-15"));
        spijk.Names.Entries.Should().Contain(e => e.Value == "Spijk" && e.ValidFrom == D("2017-08-15") && e.ValidTo == null);
        // Appingedam's three voorkomens (geometry changes) are one name.
        build.Places["places/nl/bag-wpl/3386"].Names.Entries.Should().ContainSingle();
        build.Places["places/nl/bag-wpl/3458"].Names.Entries.Should().ContainSingle(e => e.Value == "'t Zandt");
    }

    [Fact]
    public async Task Streets_follow_their_woonplaats_and_carry_PC6_postcodes()
    {
        var build = await BuildAsync();
        var street = build.Streets["streets/nl/bag/0003300000116985"];
        street.Names.Resolve(["nl"]).Should().Be("Abel Eppensstraat");
        street.Periods.Select(p => p.Municipality).Should().Equal("places/nl/cbs/GM0003", "places/nl/cbs/GM1979");
        street.Periods.Should().OnlyContain(p => p.Locality == "places/nl/bag-wpl/3386");
        street.Periods[^1].PostalCodes.Should().Equal("postalcodes/nl/9902HA", "postalcodes/nl/9902HB");

        var postal = build.PostalCodes["postalcodes/nl/9902HA"];
        postal.Localities.Should().ContainSingle(l => l.Key == "appingedam" && l.Place == "places/nl/bag-wpl/3386");
        postal.Localities[0].Municipalities.Should().ContainSingle(m => m.Id == "places/nl/cbs/GM1979");
        // Withdrawn nummeraanduidingen give no postcode; no house number is stored anywhere (RD5).
        build.PostalCodes.Should().NotContainKey("postalcodes/nl/9902JV");
        // Roads of woonplaatsen whose fixture has no nummeraanduidingen still exist, without postcodes.
        build.Streets["streets/nl/bag/0024300000144283"].Names.Resolve(["nl"]).Should().Be("'t Hoge Heem");
    }

    [Fact]
    public async Task Attribution_names_Kadaster_and_CBS()
    {
        var build = await BuildAsync();
        build.Sources["nl-bag"].Attribution.Should().Contain("Kadaster");
        build.Sources["nl-cbs-gemeenten"].Attribution.Should().Contain("Bron: CBS");
        build.Sources["nl-cbs-herindelingen"].Licence.Should().Be("CC BY 4.0");
    }

    [Fact]
    public async Task Two_builds_are_identical()
    {
        var first = await BuildAsync();
        var second = await BuildAsync();
        first.Documents().Select(ReleaseCanonical.Hash).Should().Equal(second.Documents().Select(ReleaseCanonical.Hash));
    }
}
