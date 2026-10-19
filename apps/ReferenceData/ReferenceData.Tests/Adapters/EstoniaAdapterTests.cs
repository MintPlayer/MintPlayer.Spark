using System.Globalization;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData;
using MintPlayer.Spark.ReferenceData.Build;
using MintPlayer.Spark.ReferenceData.Releases;
using MintPlayer.Spark.ReferenceData.Sources.Estonia;

namespace ReferenceData.Tests.Adapters;

/// <summary>The Estonian adapter on its real EHAK cut and doc-derived ADS extract (PRD §4.4, RS8).</summary>
public class EstoniaAdapterTests
{
    private static Task<RefDataBuild> BuildAsync()
        => new RefDatasetBuilder([new EstoniaEhakSource(), new EstoniaAdsSource()], [new EstoniaMapper()])
            .BuildFixturesAsync(CancellationToken.None);

    private static DateOnly D(string text) => DateOnly.Parse(text, CultureInfo.InvariantCulture);

    private static Func<string, Task<RefPlace?>> Loader(RefDataBuild build) => id => Task.FromResult(build.Places.GetValueOrDefault(id));

    [Fact]
    public async Task The_EHAK_dbf_gives_county_municipality_and_settlement()
    {
        var build = await BuildAsync();
        build.Ledger.Conflicts.Should().BeEmpty();
        build.Countries["countries/ee"].Profile.TimeZone.Should().Be("Europe/Tallinn");
        var kesklinn = build.Places["places/ee/ehak/0298"];
        RefAsOf.PeriodAt(kesklinn, D("2026-10-19"))!.Kind.Should().Be("ee:district");
        RefAsOf.SegmentAt(kesklinn, D("2026-10-19"))!.Ancestors.Should().Equal("places/ee/ehak/0784", "places/ee/ehak/0037", build.Countries["countries/ee"].Place!);
        // Windows-1257: "Arandi küla".
        build.Places["places/ee/ehak/1313"].Names.Entries.Should().ContainSingle(e => e.Tag == "et" && e.Value == "Arandi küla");
        build.Places["places/ee/ehak/1313"].Periods[0].Role.Should().Be(PlaceRole.Locality);
    }

    [Fact]
    public async Task A_former_municipality_continues_as_the_settlement_of_the_same_name()
    {
        var build = await BuildAsync();
        // Kuressaare linn: municipality 0349 until the 2017 reform, then settlement unit 3655 of Saaremaa vald.
        var kuressaare = build.Places["places/ee/ehak/0349"];
        build.Places.Should().NotContainKey("places/ee/ehak/3655");
        build.Ledger.Find("ehak:3655").Should().Be(kuressaare.Id);
        RefAsOf.PeriodAt(kuressaare, D("2017-10-23"))!.Kind.Should().Be("ee:municipality");
        RefAsOf.PeriodAt(kuressaare, D("2017-10-24"))!.Parent.Should().Be("places/ee/ehak/0714");
        RefAsOf.PeriodAt(kuressaare, D("2017-10-24"))!.Kind.Should().Be("ee:settlement");
        kuressaare.Names.Entries.Should().ContainSingle(e => e.Value == "Kuressaare linn" && e.ValidTo == null);
        build.Lineage.Values.Should().ContainSingle(e => e.Kind == LineageKind.Demote && e.Date == D("2017-10-24"));
        (await RefAsOf.ResolveAsync(kuressaare, D("2026-10-19"), Loader(build))).Places.Should().ContainSingle(p => p.Id == kuressaare.Id);
    }

    [Fact]
    public async Task Streets_keep_their_identity_across_the_reform_and_carry_no_postcodes()
    {
        var build = await BuildAsync();
        var lossi = build.Streets["streets/ee/ads/LP07000001"];
        lossi.Periods.Select(p => p.Municipality).Should().Equal("places/ee/ehak/0349", "places/ee/ehak/0714");
        RefAsOf.PeriodAt(lossi, D("2017-10-24"))!.Locality.Should().Be("places/ee/ehak/0349");
        lossi.Periods.Should().OnlyContain(p => p.PostalCodes.Count == 0);
        build.PostalCodes.Should().BeEmpty();
        build.Streets["streets/ee/ads/LP01000001"].Names.Entries.Should().ContainSingle(e => e.Value == "Narva mnt");
    }

    [Fact]
    public async Task Attribution_and_determinism()
    {
        var first = await BuildAsync();
        first.Sources[EstoniaEhakSource.SourceId].Attribution.Should().Contain("Estonian Land and Spatial Development Board");
        var second = await BuildAsync();
        second.Documents().Select(ReleaseCanonical.Hash).Should().Equal(first.Documents().Select(ReleaseCanonical.Hash));
    }
}
