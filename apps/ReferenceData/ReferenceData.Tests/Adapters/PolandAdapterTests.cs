using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData;
using MintPlayer.Spark.ReferenceData.Build;
using MintPlayer.Spark.ReferenceData.Releases;
using MintPlayer.Spark.ReferenceData.Sources.Poland;

namespace ReferenceData.Tests.Adapters;

/// <summary>
/// The Polish adapter (PRD §4.4, plan R8) on its real fixtures (2026-10-19): TERYT TERC/SIMC/ULIC for Sopot and Hel, and
/// their PRG address points streamed out of the Deflate64 <c>POLSKA.zip</c>.
/// </summary>
public class PolandAdapterTests
{
    private const string Sopot = "places/pl/terc/2264011";
    private const string Hel = "places/pl/terc/2211011";

    private static Task<RefDataBuild> BuildAsync()
        => new RefDatasetBuilder([new PolandTerytSource(), new PolandPrgSource()], [new PolandMapper()]).BuildFixturesAsync(CancellationToken.None);

    private static DateOnly D(string text) => DateOnly.Parse(text, CultureInfo.InvariantCulture);

    [Fact]
    public async Task The_profile_and_the_hierarchy_are_Polish()
    {
        var build = await BuildAsync();
        build.Ledger.Conflicts.Should().BeEmpty();
        var country = build.Countries["countries/pl"];
        country.Alpha3.Should().Be("POL");
        country.Profile.TimeZone.Should().Be("Europe/Warsaw");
        Regex.IsMatch("81-833", country.Profile.PostcodePattern!).Should().BeTrue();
        Regex.IsMatch("81833", country.Profile.PostcodePattern!).Should().BeFalse();

        build.Places["places/pl/terc/22"].Names.Entries.Single().Value.Should().Be("Pomorskie");
        var ancestors = RefAsOf.SegmentAt(build.Places["places/pl/simc/0934820"], D("2026-10-19"))!.Ancestors;
        // Przylesie is a part of Sopot (SYMPOD), in gmina Sopot, in the city county Sopot, in pomorskie.
        ancestors.Should().Equal("places/pl/simc/0934783", Sopot, "places/pl/terc/2264", "places/pl/terc/22", "places/pl/iso3166/PL");
        RefAsOf.PeriodAt(build.Places["places/pl/simc/0934820"], D("2026-10-19"))!.Kind.Should().Be("pl:locality-part");
        RefAsOf.PeriodAt(build.Places["places/pl/simc/0934783"], D("2026-10-19"))!.Kind.Should().Be("pl:locality");
        build.Places["places/pl/simc/0934783"].Codes.Should().Contain(c => c.Scheme == "simc-rm" && c.Value == "96");
    }

    [Fact]
    public async Task A_street_name_key_is_not_a_street()
    {
        // SYM_UL 00656 "Bałtycka" exists in Sopot and in Hel: two streets.
        var build = await BuildAsync();
        build.Streets["streets/pl/ulic/0934783-00656"].Periods.Single().Municipality.Should().Be(Sopot);
        build.Streets["streets/pl/ulic/0934501-00656"].Periods.Single().Municipality.Should().Be(Hel);
    }

    [Fact]
    public async Task Street_names_carry_the_feature_and_the_given_name()
    {
        var build = await BuildAsync();
        var abrahama = build.Streets["streets/pl/ulic/0934783-00090"];
        abrahama.Names.Entries.Should().Contain(e => e.Value == "ul. Antoniego Abrahama" && e.Kind == TranslatedStringKind.Official);
        abrahama.Names.Entries.Should().Contain(e => e.Value == "Antoniego Abrahama" && e.Kind == TranslatedStringKind.Common);
        abrahama.Names.Entries.Should().Contain(e => e.Value == "Abrahama" && e.Kind == TranslatedStringKind.Common);
        build.Streets["streets/pl/ulic/0934783-13132"].Names.Entries.Select(e => e.Value).Should().Equal("ul. Młyńska", "Młyńska");
    }

    [Fact]
    public async Task Postcodes_come_from_the_PRG_address_points_and_are_flagged_unvalidated()
    {
        var build = await BuildAsync();
        var hel = build.PostalCodes["postalcodes/pl/84-150"];
        hel.Localities.Should().ContainSingle(l => l.Place == "places/pl/simc/0934501" && l.Municipalities.Any(m => m.Id == Hel));
        hel.Source.Should().Equal(PolandPrgSource.SourceId);

        var abrahama = build.Streets["streets/pl/ulic/0934783-00090"].Periods.Single();
        abrahama.PostalCodes.Should().Equal("postalcodes/pl/81-833");
        abrahama.Locality.Should().Be("places/pl/simc/0934783");
        build.Streets["streets/pl/ulic/0934501-19142"].Periods.Single().PostalCodes.Should().Equal("postalcodes/pl/84-150");

        build.Sources["pl-prg"].Attribution.Should().Contain("not validated by Poczta Polska");
        build.Sources["pl-teryt"].Attribution.Should().StartWith("Źródło: GUS, TERYT, CC BY 4.0, stan na");
    }

    [Fact]
    public void The_change_check_hash_ignores_the_STAN_NA_column()
    {
        var monday = Encoding.UTF8.GetBytes("WOJ;NAZWA;STAN_NA\r\n22;POMORSKIE;2026-10-19\r\n");
        var tuesday = Encoding.UTF8.GetBytes("WOJ;NAZWA;STAN_NA\r\n22;POMORSKIE;2026-10-20\r\n");
        var renamed = Encoding.UTF8.GetBytes("WOJ;NAZWA;STAN_NA\r\n22;POMORZE;2026-10-20\r\n");
        PolandTerytSource.ContentHash([monday]).Should().Be(PolandTerytSource.ContentHash([tuesday]));
        PolandTerytSource.ContentHash([monday]).Should().NotBe(PolandTerytSource.ContentHash([renamed]));
    }

    [Fact]
    public async Task Two_builds_give_the_same_bytes()
    {
        var first = await BuildAsync();
        var second = await BuildAsync();
        first.Documents().Select(d => ReleaseCanonical.Hash(d)).Should().Equal(second.Documents().Select(d => ReleaseCanonical.Hash(d)));
    }
}
