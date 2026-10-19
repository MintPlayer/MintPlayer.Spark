using System.Globalization;
using System.Text.RegularExpressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData;
using MintPlayer.Spark.ReferenceData.Build;
using MintPlayer.Spark.ReferenceData.Releases;
using MintPlayer.Spark.ReferenceData.Sources.Australia;

namespace ReferenceData.Tests.Adapters;

/// <summary>
/// The Australian adapter (PRD §4.4, plan R8) on real G-NAF tables (August 2026 release, ACT), read by range requests
/// from the release ZIP: the state, five localities (gazetted, topographic and district rows), street localities and
/// the postcodes of their addresses.
/// </summary>
public class AustraliaAdapterTests
{
    private const string Reid = "places/au/gnaf/loc537c3f114213";

    private static Task<RefDataBuild> BuildAsync()
        => new RefDatasetBuilder([new AustraliaGnafSource()], [new AustraliaMapper()]).BuildFixturesAsync(CancellationToken.None);

    private static DateOnly D(string text) => DateOnly.Parse(text, CultureInfo.InvariantCulture);

    [Fact]
    public async Task The_profile_and_the_state_are_Australian()
    {
        var build = await BuildAsync();
        build.Ledger.Conflicts.Should().BeEmpty();
        var country = build.Countries["countries/au"];
        country.Alpha3.Should().Be("AUS");
        country.Profile.TimeZone.Should().BeNull();
        country.Profile.MunicipalityLevel.Should().Be("au:locality");
        Regex.IsMatch("2612", country.Profile.PostcodePattern!).Should().BeTrue();
        var act = build.Places["places/au/state/ACT"];
        act.Names.Entries.Should().ContainSingle(e => e.Value == "Australian Capital Territory");
        act.TimeZone.Should().Be("Australia/Sydney");
        RefAsOf.PeriodAt(act, D("2026-10-01"))!.Parent.Should().Be("places/au/iso3166/AU");
    }

    [Fact]
    public async Task Localities_and_districts_are_places_of_their_own_kind()
    {
        var build = await BuildAsync();
        var reid = build.Places[Reid];
        reid.Names.Entries.Should().ContainSingle(e => e.Value == "Reid");
        RefAsOf.SegmentAt(reid, D("2026-10-01"))!.Ancestors.Should().Equal("places/au/state/ACT", "places/au/iso3166/AU");
        reid.Periods.Single().Kind.Should().Be("au:locality");
        reid.TimeZone.Should().Be("Australia/Sydney");
        build.Places["places/au/gnaf/locdf08084d164d"].Periods.Single().Kind.Should().Be("au:district");
        build.Places["places/au/gnaf/500223393"].Periods.Single().Kind.Should().Be("au:locality");
    }

    [Fact]
    public async Task Street_localities_carry_the_street_and_its_postcodes()
    {
        var build = await BuildAsync();
        var allambee = build.Streets["streets/au/gnaf/ACT466"];
        allambee.Names.Entries.Should().Contain(e => e.Value == "Allambee Street" && e.Kind == TranslatedStringKind.Official);
        allambee.Names.Entries.Should().Contain(e => e.Value == "Allambee St" && e.Kind == TranslatedStringKind.Common);
        var period = RefAsOf.PeriodAt(allambee, D("2026-10-01"))!;
        period.Municipality.Should().Be(Reid);
        period.PostalCodes.Should().Equal("postalcodes/au/2612");
        // One street (G-NAF street PID 502489991) in several localities: one street locality each.
        build.Streets.Values.Count(s => s.Codes.Any(c => c.Scheme == "gnaf-street" && c.Value == "502489991")).Should().Be(3);
        build.Streets["streets/au/gnaf/ACT3615018"].Names.Entries.Should().Contain(e => e.Value == "Drake-Brockman Drive");
    }

    [Fact]
    public async Task Postcodes_come_from_the_addresses_and_name_their_localities()
    {
        var build = await BuildAsync();
        build.PostalCodes["postalcodes/au/2612"].Localities.Select(l => l.Key).Should().BeEquivalentTo(new[] { "braddon", "reid", "turner" });
        build.PostalCodes["postalcodes/au/2618"].Localities.Should().ContainSingle(l => l.Key == "hall");
        // G-NAF's "no postcode" placeholder never becomes a postal code.
        build.PostalCodes.ContainsKey("postalcodes/au/9999").Should().BeFalse();
    }

    [Fact]
    public async Task The_credit_line_passes_on_the_eula_and_its_mail_restriction()
    {
        var build = await BuildAsync();
        var attribution = build.Sources["au-gnaf"].Attribution;
        attribution.Should().StartWith("Incorporates or developed using G-NAF © Geoscape Australia licensed by the Commonwealth of Australia under the Open Geo-coded National Address File (G-NAF) End User Licence Agreement.");
        attribution.Should().Contain("must not use the Licensed Material for the generation of an address or a compilation of addresses for the sending of mail");
        build.Sources["au-gnaf"].Licence.Should().Be("Open G-NAF End User Licence Agreement");
    }

    [Theory]
    [InlineData("AUSTRALIAN CAPITAL TERRITORY", "Australian Capital Territory")]
    [InlineData("DRAKE-BROCKMAN", "Drake-Brockman")]
    [InlineData("O'CONNOR", "O'Connor")]
    [InlineData("PRINCE OF WALES", "Prince of Wales")]
    public void Capital_names_are_title_cased(string upper, string title)
        => AustraliaMapper.Title(upper).Should().Be(title);

    [Fact]
    public void Broken_Hill_and_Lord_Howe_keep_their_own_zones()
    {
        AustraliaProfile.PostcodeZones["2880"].Should().Be("Australia/Broken_Hill");
        AustraliaProfile.PostcodeZones["2898"].Should().Be("Australia/Lord_Howe");
    }

    [Fact]
    public async Task Two_builds_give_the_same_hashes()
    {
        var first = await BuildAsync();
        var second = await BuildAsync();
        first.Documents().Select(ReleaseCanonical.Hash).Should().Equal(second.Documents().Select(ReleaseCanonical.Hash));
    }
}
