using System.Globalization;
using System.Text.RegularExpressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData;
using MintPlayer.Spark.ReferenceData.Build;
using MintPlayer.Spark.ReferenceData.Releases;
using MintPlayer.Spark.ReferenceData.Sources.Slovenia;

namespace ReferenceData.Tests.Adapters;

/// <summary>
/// The Slovenian adapter (PRD §4.4, plan R8) on real GURS WFS responses (2026-10-19): Piran and Ankaran, bilingual
/// Italian–Slovenian municipalities, with the address register linking streets to postal areas.
/// </summary>
public class SloveniaAdapterTests
{
    private const string Piran = "places/si/obcina/90";
    private const string PortorozSettlement = "places/si/naselje/110300000101230112";

    private static Task<RefDataBuild> BuildAsync()
        => new RefDatasetBuilder([new SloveniaUnitsSource(), new SloveniaAddressesSource()], [new SloveniaMapper()])
            .BuildFixturesAsync(CancellationToken.None);

    private static DateOnly D(string text) => DateOnly.Parse(text, CultureInfo.InvariantCulture);

    [Fact]
    public async Task The_profile_and_the_country_place_are_Slovenian()
    {
        var build = await BuildAsync();
        build.Ledger.Conflicts.Should().BeEmpty();
        var country = build.Countries["countries/si"];
        country.Alpha3.Should().Be("SVN");
        country.Profile.TimeZone.Should().Be("Europe/Ljubljana");
        Regex.IsMatch("6330", country.Profile.PostcodePattern!).Should().BeTrue();
        Regex.IsMatch("SI-6330", country.Profile.PostcodePattern!).Should().BeTrue();
        country.Profile.Levels.Single(l => l.Key == "si:region").Optional.Should().BeTrue();
        country.Place.Should().Be("places/si/iso3166/SI");
    }

    [Fact]
    public async Task Bilingual_municipalities_carry_their_Italian_official_name()
    {
        var build = await BuildAsync();
        var piran = build.Places[Piran];
        piran.Names.Entries.Should().Contain(e => e.Tag == "sl" && e.Value == "Piran" && e.Kind == TranslatedStringKind.Official);
        piran.Names.Entries.Should().Contain(e => e.Tag == "it" && e.Value == "Pirano" && e.Kind == TranslatedStringKind.Official);
        piran.OfficialLanguages.Select(l => l.Lang).Should().Equal("sl", "it");
        build.Places["places/si/obcina/213"].Names.Entries.Should().Contain(e => e.Tag == "it" && e.Value == "Ancarano");
        build.Places[PortorozSettlement].Names.Entries.Should().Contain(e => e.Tag == "it" && e.Value == "Portorose");
    }

    [Fact]
    public async Task The_address_register_gives_a_municipality_its_statistical_region()
    {
        var build = await BuildAsync();
        RefAsOf.SegmentAt(build.Places[PortorozSettlement], D("2026-10-01"))!.Ancestors.Should().Equal(
            Piran, "places/si/statreg/12", "places/si/iso3166/SI");
        build.Places["places/si/statreg/12"].Names.Entries.Should().Contain(e => e.Value == "Obalno-kraška");
        // Ankaran has no address rows in the cut: it stays under the country (the region level is optional).
        RefAsOf.PeriodAt(build.Places["places/si/obcina/213"], D("2026-10-01"))!.Parent.Should().Be("places/si/iso3166/SI");
    }

    [Fact]
    public async Task Streets_are_linked_to_postal_codes_through_the_address_register()
    {
        var build = await BuildAsync();
        var adamiceva = build.Streets["streets/si/eid/110400000162285756"];
        adamiceva.Names.Entries.Should().Contain(e => e.Tag == "it" && e.Value == "Via Louis Adamič");
        RefAsOf.PeriodAt(adamiceva, D("2026-10-01"))!.PostalCodes.Should().Equal("postalcodes/si/6330");
        // Arze lies in the settlement Portorož but in postal area 6330 Piran.
        var arze = RefAsOf.PeriodAt(build.Streets["streets/si/eid/110400000162294238"], D("2026-10-01"))!;
        arze.Locality.Should().Be(PortorozSettlement);
        arze.PostalCodes.Should().Equal("postalcodes/si/6330");
    }

    [Fact]
    public async Task A_bilingual_postal_name_is_split_per_language()
    {
        var build = await BuildAsync();
        build.PostalCodes["postalcodes/si/6320"].Localities.Should().ContainSingle();
        var locality = build.PostalCodes["postalcodes/si/6320"].Localities[0];
        locality.Names.Entries.Should().Contain(e => e.Tag == "sl" && e.Value == "Portorož");
        locality.Names.Entries.Should().Contain(e => e.Tag == "it" && e.Value == "Portorose");
        locality.Place.Should().Be(PortorozSettlement);
        locality.Municipalities.Should().ContainSingle(m => m.Id == Piran);
    }

    [Fact]
    public async Task The_credit_lines_name_the_register_and_the_year()
    {
        var build = await BuildAsync();
        build.Sources["si-rn"].Attribution.Should().StartWith("Geodetska uprava Republike Slovenije, Register naslovov, 2026");
        build.Sources["si-rpe"].Attribution.Should().StartWith("Geodetska uprava Republike Slovenije, Register prostorskih enot, 2026");
    }

    [Fact]
    public async Task Two_builds_give_the_same_hashes()
    {
        var first = await BuildAsync();
        var second = await BuildAsync();
        first.Documents().Select(ReleaseCanonical.Hash).Should().Equal(second.Documents().Select(ReleaseCanonical.Hash));
    }
}
