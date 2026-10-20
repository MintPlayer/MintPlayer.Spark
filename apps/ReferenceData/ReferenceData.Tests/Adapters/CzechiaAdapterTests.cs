using System.Globalization;
using System.Text.RegularExpressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData;
using MintPlayer.Spark.ReferenceData.Build;
using MintPlayer.Spark.ReferenceData.Releases;
using MintPlayer.Spark.ReferenceData.Sources.Czechia;

namespace ReferenceData.Tests.Adapters;

/// <summary>
/// The Czech adapter (PRD §4.4, plan R8) on its real fixtures: RÚIAN's territorial structure (VFR XML, 2026-09-30 state)
/// and the address-place CSVs of Liberec and Cetenov (Windows-1250).
/// </summary>
public class CzechiaAdapterTests
{
    private static Task<RefDataBuild> BuildAsync()
        => new RefDatasetBuilder([new CzechiaUnitsSource(), new CzechiaAddressesSource()], [new CzechiaMapper()])
            .BuildFixturesAsync(CancellationToken.None);

    private static DateOnly D(string text) => DateOnly.Parse(text, CultureInfo.InvariantCulture);

    [Fact]
    public async Task The_profile_and_the_country_place_are_Czech()
    {
        var build = await BuildAsync();
        build.Ledger.Conflicts.Should().BeEmpty();
        var country = build.Countries["countries/cz"];
        country.Alpha3.Should().Be("CZE");
        country.Profile.TimeZone.Should().Be("Europe/Prague");
        country.Profile.MunicipalityLevel.Should().Be("cz:municipality");
        Regex.IsMatch("46001", country.Profile.PostcodePattern!).Should().BeTrue();
        Regex.IsMatch("460 01", country.Profile.PostcodePattern!).Should().BeTrue();
        Regex.IsMatch("860 01", country.Profile.PostcodePattern!).Should().BeFalse();
        Regex.IsMatch("21/13", country.Profile.HouseNumbers!.NumberPattern).Should().BeTrue();
        country.Place.Should().Be("places/cz/iso3166/CZ");
        build.Places["places/cz/iso3166/CZ"].Codes.Should().Contain(c => c.Scheme == "ruian-stat" && c.Value == "1");
    }

    [Fact]
    public async Task The_hierarchy_runs_kraj_okres_obec_cast_obce()
    {
        var build = await BuildAsync();
        var part = build.Places["places/cz/cobce/408638"];
        part.Names.Entries.Should().ContainSingle(e => e.Tag == "cs" && e.Value == "Liberec I-Staré Město");
        RefAsOf.SegmentAt(part, D("2026-10-01"))!.Ancestors.Should().Equal(
            "places/cz/obec/563889", "places/cz/okres/3505", "places/cz/vusc/78", "places/cz/iso3166/CZ");
        build.Places["places/cz/vusc/78"].Names.Entries.Should().Contain(e => e.Value == "Liberecký kraj");
        build.Places["places/cz/vusc/78"].Codes.Should().Contain(c => c.Scheme == "nuts" && c.Value == "CZ051");
        // Periods start at DatumVzniku: the kraje were created on 2000-01-01.
        build.Places["places/cz/vusc/78"].Lifetime.From.Should().Be(D("2000-01-01"));
        build.Places["places/cz/obec/563943"].Lifetime.From.Should().Be(D("1990-09-01"));
        // A unit's container shares its name in VFR (<vf:Vusc> around <vf:Vusc gml:id=…>): only units became places.
        build.Places.Keys.Count(k => k.StartsWith("places/cz/vusc/", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public async Task Windows_1250_names_are_read_correctly()
    {
        var build = await BuildAsync();
        build.Streets["streets/cz/ulice/252000"].Names.Entries.Should().ContainSingle(e => e.Value == "nám. Dr. E. Beneše");
        build.Places["places/cz/cobce/17655"].Names.Entries.Should().Contain(e => e.Value == "Těšnov");
    }

    [Fact]
    public async Task Streets_link_their_obec_part_and_psc()
    {
        var build = await BuildAsync();
        var street = build.Streets["streets/cz/ulice/252433"];
        street.Names.Entries.Should().ContainSingle(e => e.Value == "Jablonecká");
        var period = RefAsOf.PeriodAt(street, D("2026-10-01"))!;
        period.Municipality.Should().Be("places/cz/obec/563889");
        period.Locality.Should().Be("places/cz/cobce/408638");
        period.PostalCodes.Should().Equal("postalcodes/cz/46001");
        // No house numbers are kept: the street carries no number field, and no document is keyed by an address place.
        build.Streets.Keys.Should().OnlyContain(k => k.StartsWith("streets/cz/ulice/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_obec_without_a_street_network_still_gets_its_postal_codes()
    {
        var build = await BuildAsync();
        build.Streets.Values.Should().NotContain(s => s.Periods.Any(p => p.Municipality == "places/cz/obec/563943"));
        foreach (var code in new[] { "46348", "46352" })
        {
            var postal = build.PostalCodes["postalcodes/cz/" + code];
            postal.Localities.Should().Contain(l => l.Key == "cetenov" && l.Municipalities.Any(m => m.Id == "places/cz/obec/563943"));
        }
    }

    [Fact]
    public async Task The_credit_line_names_the_year_of_the_data()
    {
        var build = await BuildAsync();
        build.Sources["cz-ruian-units"].Attribution.Should().StartWith("ČÚZK, 2026");
        build.Sources["cz-ruian-addresses"].Licence.Should().Be("CC BY 4.0");
    }

    [Fact]
    public void The_change_check_looks_for_the_last_month_end()
        => RuianMonthlySource.LastMonthEnd(D("2026-10-19")).Should().Be(D("2026-09-30"));

    [Fact]
    public async Task Two_builds_give_the_same_hashes()
    {
        var first = await BuildAsync();
        var second = await BuildAsync();
        first.Documents().Select(ReleaseCanonical.Hash).Should().Equal(second.Documents().Select(ReleaseCanonical.Hash));
    }
}
