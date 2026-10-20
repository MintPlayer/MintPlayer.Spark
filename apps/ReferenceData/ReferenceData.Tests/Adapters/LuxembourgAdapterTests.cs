using System.Text.RegularExpressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData;
using MintPlayer.Spark.ReferenceData.Build;
using MintPlayer.Spark.ReferenceData.Releases;
using MintPlayer.Spark.ReferenceData.Sources.Luxembourg;

namespace ReferenceData.Tests.Adapters;

/// <summary>
/// The Luxembourg adapter (PRD §4.4, plan R8) on its real CACLR fixture (fixed-width ISO-8859-1): cantons, the current
/// commune Groussbus-Wal (Grosbous + Wahl, 2023) and Esch-sur-Alzette, localities, streets and postcodes from buildings.
/// </summary>
public class LuxembourgAdapterTests
{
    private static Task<RefDataBuild> BuildAsync()
        => new RefDatasetBuilder([new LuxembourgCaclrSource()], [new LuxembourgMapper()]).BuildFixturesAsync(CancellationToken.None);

    private static DateOnly D(string text) => DateOnly.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task The_profile_and_the_country_place_are_Luxembourgish()
    {
        var build = await BuildAsync();
        build.Ledger.Conflicts.Should().BeEmpty();
        var country = build.Countries["countries/lu"];
        country.Alpha3.Should().Be("LUX");
        country.Profile.TimeZone.Should().Be("Europe/Luxembourg");
        country.Profile.OfficialLanguages.Select(l => l.Lang).Should().Equal("fr", "de", "lb");
        Regex.IsMatch("8838", country.Profile.PostcodePattern!).Should().BeTrue();
        Regex.IsMatch("L-8838", country.Profile.PostcodePattern!).Should().BeTrue();
        country.Place.Should().Be("places/lu/iso3166/LU");
    }

    [Fact]
    public async Task Cantons_are_title_cased_and_communes_are_keyed_on_canton_and_code()
    {
        var build = await BuildAsync();
        build.Places["places/lu/canton/08"].Names.Resolve(["fr"]).Should().Be("Redange");
        build.Places["places/lu/canton/03"].Names.Resolve(["fr"]).Should().Be("Esch-sur-Alzette");
        var commune = build.Places["places/lu/commune/0814"];
        commune.Names.Resolve(["fr"]).Should().Be("Groussbus-Wal");
        RefAsOf.PeriodAt(commune, D("2026-01-01"))!.Parent.Should().Be("places/lu/canton/08");
        RefAsOf.SegmentAt(commune, D("2026-01-01"))!.Ancestors.Should().Equal("places/lu/canton/08", "places/lu/iso3166/LU");
    }

    [Fact]
    public async Task Former_communes_are_skipped_because_CACLR_states_no_legal_end_date()
    {
        var build = await BuildAsync();
        build.Places.Should().NotContainKey("places/lu/commune/0807");
        build.Places.Should().NotContainKey("places/lu/commune/0813");
        build.Ledger.Find("lu-commune:0813").Should().BeNull();
        build.Warnings.Should().Contain(w => w.Contains("0813") && w.Contains("Wahl"));
    }

    [Fact]
    public async Task Localities_hang_under_their_commune()
    {
        var build = await BuildAsync();
        var wahl = build.Places["places/lu/localite/00356"];
        wahl.Names.Resolve(["fr"]).Should().Be("Wahl");
        RefAsOf.PeriodAt(wahl, D("2026-01-01"))!.Parent.Should().Be("places/lu/commune/0814");
        RefAsOf.PeriodAt(wahl, D("2026-01-01"))!.Kind.Should().Be("lu:locality");
    }

    [Fact]
    public async Task Streets_get_postcodes_from_buildings_and_the_postcode_label_is_the_city_line()
    {
        var build = await BuildAsync();
        // ISO-8859-1 decoded: è.
        build.Streets["streets/lu/caclr/04540"].Names.Resolve(["fr"]).Should().Be("Rue du Cimetière");

        var amEck = build.Streets["streets/lu/caclr/04726"];
        amEck.Periods.Should().ContainSingle();
        amEck.Periods[0].Municipality.Should().Be("places/lu/commune/0814");
        amEck.Periods[0].Locality.Should().Be("places/lu/localite/00356");
        amEck.Periods[0].PostalCodes.Should().Equal("postalcodes/lu/8838");
        build.PostalCodes["postalcodes/lu/8838"].Localities.Should().ContainSingle(l => l.Key == "wahl" && l.Place == "places/lu/localite/00356" && l.Main);

        // A street of Wahl served by postcode 8818 "GREVELS": the postal locality is Grevels, not Wahl.
        build.Streets["streets/lu/caclr/04728"].Periods[0].PostalCodes.Should().Equal("postalcodes/lu/8818");
        build.PostalCodes["postalcodes/lu/8818"].Localities.Should().ContainSingle(l => l.Key == "grevels" && l.Place == null);

        // ESCH/ALZETTE is title-cased.
        build.PostalCodes["postalcodes/lu/4091"].Localities[0].Names.Resolve(["fr"]).Should().Be("Esch/Alzette");
        build.PostalCodes["postalcodes/lu/4091"].Localities[0].Municipalities.Should().ContainSingle(m => m.Id == "places/lu/commune/0304");
    }

    [Fact]
    public async Task Attribution_credits_ACT_although_CC0_needs_none()
    {
        var build = await BuildAsync();
        build.Sources["lu-caclr"].Licence.Should().Be("CC0 1.0");
        build.Sources["lu-caclr"].Attribution.Should().Contain("CACLR");
    }

    [Fact]
    public async Task Two_builds_are_identical()
    {
        var first = await BuildAsync();
        var second = await BuildAsync();
        first.Documents().Select(ReleaseCanonical.Hash).Should().Equal(second.Documents().Select(ReleaseCanonical.Hash));
    }
}
