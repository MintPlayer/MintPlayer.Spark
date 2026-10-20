using System.Text.RegularExpressions;
using MintPlayer.Spark.ReferenceData;
using MintPlayer.Spark.ReferenceData.Build;
using MintPlayer.Spark.ReferenceData.Releases;
using MintPlayer.Spark.ReferenceData.Sources.Liechtenstein;

namespace ReferenceData.Tests.Adapters;

/// <summary>The Liechtenstein adapter (PRD §4.4, plan R8) on its real fixture: swisstopo's address directory for Liechtenstein.</summary>
public class LiechtensteinAdapterTests
{
    private static Task<RefDataBuild> BuildAsync()
        => new RefDatasetBuilder([new LiechtensteinAddressesSource()], [new LiechtensteinMapper()]).BuildFixturesAsync(CancellationToken.None);

    [Fact]
    public async Task The_profile_is_Liechtensteins()
    {
        var build = await BuildAsync();
        var country = build.Countries["countries/li"];
        country.Alpha3.Should().Be("LIE");
        country.Profile.TimeZone.Should().Be("Europe/Vaduz");
        (country.Profile.HistoryKnownSince is null).Should().BeTrue();
        Regex.IsMatch("9493", country.Profile.PostcodePattern!).Should().BeTrue();
        Regex.IsMatch("9500", country.Profile.PostcodePattern!).Should().BeFalse();
        country.Place.Should().Be("places/li/iso3166/LI");
    }

    [Fact]
    public async Task Gemeinden_are_keyed_by_FOSNR_under_the_country()
    {
        var build = await BuildAsync();
        foreach (var fosnr in new[] { "7001", "7006", "7008", "7009" })
        {
            var gemeinde = build.Places[$"places/li/fosnr/{fosnr}"];
            gemeinde.Periods.Should().ContainSingle(p => p.Parent == "places/li/iso3166/LI" && p.Kind == "li:municipality");
            gemeinde.TimeZone.Should().Be("Europe/Vaduz");
        }
        build.Places["places/li/fosnr/7001"].Names.Entries.Single().Value.Should().Be("Vaduz");
    }

    [Fact]
    public async Task Planned_addresses_are_skipped_and_no_house_number_is_kept()
    {
        var build = await BuildAsync();
        // Schaanerstrasse, Im Häldele, Backofengasse and Steinbruch occur only with planned addresses in the fixture.
        foreach (var esid in new[] { "89440085", "89460011", "89530004", "89510022" })
            build.Streets.ContainsKey($"streets/li/esid/{esid}").Should().BeFalse();
        build.Streets["streets/li/esid/89440001"].Names.Entries.Single().Value.Should().Be("Abtswingertweg");
        build.Documents().Select(d => ReleaseCanonical.Serialize(d)).Any(json => json.Contains("ADR_NUMBER", StringComparison.Ordinal)).Should().BeFalse();
    }

    [Fact]
    public async Task Postal_localities_drop_the_FL_suffix_and_cross_Gemeinde_borders()
    {
        var build = await BuildAsync();
        var mauren = build.PostalCodes["postalcodes/li/9493"].Localities.Single();
        mauren.Key.Should().Be("mauren");
        mauren.Names.Entries.Single().Value.Should().Be("Mauren");

        var gamprin = build.PostalCodes["postalcodes/li/9487"].Localities.Single();
        gamprin.Municipalities.Select(m => m.Id).Order(StringComparer.Ordinal).Should().Equal("places/li/fosnr/7001", "places/li/fosnr/7009");
        build.Streets["streets/li/esid/89440127"].Periods.Single().PostalCodes.Should().Equal("postalcodes/li/9487");
    }

    [Fact]
    public async Task The_credit_is_swisstopos()
    {
        var build = await BuildAsync();
        build.Sources[LiechtensteinAddressesSource.SourceId].Attribution.StartsWith("©swisstopo", StringComparison.Ordinal).Should().BeTrue();
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
