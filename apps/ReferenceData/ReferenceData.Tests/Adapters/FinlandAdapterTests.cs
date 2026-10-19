using System.Globalization;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData;
using MintPlayer.Spark.ReferenceData.Build;
using MintPlayer.Spark.ReferenceData.Releases;
using MintPlayer.Spark.ReferenceData.Sources.Finland;

namespace ReferenceData.Tests.Adapters;

/// <summary>The Finnish adapter on its real Statistics Finland and Ryhti cuts (PRD §4.4, RS8).</summary>
public class FinlandAdapterTests
{
    private static Task<RefDataBuild> BuildAsync()
        => new RefDatasetBuilder([new FinlandClassificationSource(), new FinlandRyhtiSource()], [new FinlandMapper()])
            .BuildFixturesAsync(CancellationToken.None);

    private static DateOnly D(string text) => DateOnly.Parse(text, CultureInfo.InvariantCulture);

    [Fact]
    public async Task The_profile_marks_Aland_as_a_VAT_territory()
    {
        var build = await BuildAsync();
        build.Ledger.Conflicts.Should().BeEmpty();
        var profile = build.Countries["countries/fi"].Profile;
        profile.TimeZone.Should().Be("Europe/Helsinki");
        profile.HistoryKnownSince.Should().Be(D("1970-01-01"));
        profile.VatTerritories.Should().ContainSingle(t => t.Code == "AX" && System.Text.RegularExpressions.Regex.IsMatch("22100", t.PostcodePattern!));
        System.Text.RegularExpressions.Regex.IsMatch("38700", profile.PostcodePattern!).Should().BeTrue();
    }

    [Fact]
    public async Task Municipalities_hang_under_their_region_with_official_languages_from_the_language_status()
    {
        var build = await BuildAsync();
        var helsinki = build.Places["places/fi/kunta/091"];
        RefAsOf.PeriodAt(helsinki, D("2021-06-01"))!.Parent.Should().Be("places/fi/maakunta/01");
        RefAsOf.SegmentAt(helsinki, D("2021-06-01"))!.Ancestors.Should().Contain(build.Countries["countries/fi"].Place!);
        // Bilingual, Finnish majority: both names official.
        helsinki.Names.Entries.Should().Contain(e => e.Tag == "fi" && e.Value == "Helsinki" && e.Kind == TranslatedStringKind.Official);
        helsinki.Names.Entries.Should().Contain(e => e.Tag == "sv" && e.Value == "Helsingfors" && e.Kind == TranslatedStringKind.Official);
        helsinki.OfficialLanguages.Select(l => l.Lang).Should().Equal("fi", "sv");

        // Swedish only: the Finnish name is an exonym.
        var mariehamn = build.Places["places/fi/kunta/478"];
        mariehamn.Names.Entries.Should().Contain(e => e.Tag == "sv" && e.Value == "Mariehamn" && e.Kind == TranslatedStringKind.Official);
        mariehamn.Names.Entries.Should().Contain(e => e.Tag == "fi" && e.Value == "Maarianhamina" && e.Kind == TranslatedStringKind.Exonym);
        mariehamn.OfficialLanguages.Select(l => l.Lang).Should().Equal("sv");

        // Finnish only, with the same Swedish spelling: no second name.
        build.Places["places/fi/kunta/214"].Names.Entries.Should().ContainSingle();
    }

    [Fact]
    public async Task A_code_that_leaves_the_list_ends_its_place_unresolved()
    {
        var build = await BuildAsync();
        var honkajoki = build.Places["places/fi/kunta/099"];
        honkajoki.Lifetime.To.Should().Be(D("2021-01-01"));
        honkajoki.Codes.Should().ContainSingle(c => c.Value == "099" && c.To == D("2021-01-01"));
        honkajoki.LineageUnresolved.Should().BeTrue();
        build.Lineage["lineage/fi/2021-01-01/absorb/kunta-099"].Unresolved.Should().BeTrue();
        // A municipality in the latest list stays open.
        (build.Places["places/fi/kunta/214"].Lifetime.To is null).Should().BeTrue();
    }

    [Fact]
    public async Task Streets_and_postcodes_come_from_Ryhti()
    {
        var build = await BuildAsync();
        var kankaanpaa = "places/fi/kunta/214";
        var haarantie = build.Streets["streets/fi/k214/haarantie"];
        haarantie.Periods.Should().ContainSingle(p => p.Municipality == kankaanpaa);
        haarantie.Periods[0].PostalCodes.Should().Equal("postalcodes/fi/38700", "postalcodes/fi/38720");

        var albertinkatu = build.Streets["streets/fi/k091/albertinkatu"];
        albertinkatu.Names.Entries.Select(e => (e.Tag, e.Value)).Should().Equal(("fi", "Albertinkatu"), ("sv", "Albertsgatan"));

        // Helsinki's postal office in both languages; Kankaanpää's in Finnish, title-cased.
        var localities = build.PostalCodes["postalcodes/fi/00150"].Localities;
        localities.Should().HaveCount(1);
        localities[0].Names.Entries.Select(e => e.Value).Should().Equal("Helsinki", "Helsingfors");
        build.PostalCodes["postalcodes/fi/38720"].Localities[0].Names.Entries[0].Value.Should().Be("Vihteljärvi");
        // Åland: a Swedish-only street.
        build.Streets.Values.Should().Contain(s => s.Names.Entries.Any(e => e.Tag == "sv" && e.Value == "Ålandsvägen"));
    }

    [Fact]
    public async Task Attribution_and_determinism()
    {
        var first = await BuildAsync();
        first.Sources[FinlandClassificationSource.SourceId].Attribution.Should().Contain("Source: Statistics Finland");
        first.Sources[FinlandRyhtiSource.SourceId].Attribution.Should().Contain("Ryhti");
        var second = await BuildAsync();
        second.Documents().Select(ReleaseCanonical.Hash).Should().Equal(first.Documents().Select(ReleaseCanonical.Hash));
    }
}
