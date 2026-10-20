using System.Globalization;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData;
using MintPlayer.Spark.ReferenceData.Build;
using MintPlayer.Spark.ReferenceData.Releases;
using MintPlayer.Spark.ReferenceData.Sources.Norway;

namespace ReferenceData.Tests.Adapters;

/// <summary>The Norwegian adapter on its real KLASS, Kartverket and Bring cuts (PRD §4.4, RS8).</summary>
public class NorwayAdapterTests
{
    private static Task<RefDataBuild> BuildAsync()
        => new RefDatasetBuilder([new NorwayKlassSource(), new NorwayAddressSource(), new NorwayPostcodeSource()], [new NorwayMapper()])
            .BuildFixturesAsync(CancellationToken.None);

    private static DateOnly D(string text) => DateOnly.Parse(text, CultureInfo.InvariantCulture);

    private static Func<string, Task<RefPlace?>> Loader(RefDataBuild build) => id => Task.FromResult(build.Places.GetValueOrDefault(id));

    [Fact]
    public async Task The_profile_and_the_country_place_are_written()
    {
        var build = await BuildAsync();
        build.Ledger.Conflicts.Should().BeEmpty();
        var country = build.Countries["countries/no"];
        country.Profile.MunicipalityLevel.Should().Be("no:municipality");
        country.Profile.TimeZone.Should().Be("Europe/Oslo");
        System.Text.RegularExpressions.Regex.IsMatch("9146", country.Profile.PostcodePattern!).Should().BeTrue();
        build.Places[country.Place!].Names.Entries.Should().Contain(e => e.Tag == "no" && e.Value == "Norge" && e.Kind == TranslatedStringKind.Official);
    }

    [Fact]
    public async Task A_recode_keeps_the_place_and_dates_its_codes()
    {
        var build = await BuildAsync();
        // Kåfjord: 1940 until 2020, 5426 until 2024, 5540 since: one place (kommunenummer is not stable).
        var kafjord = build.Places["places/no/knr/1940"];
        kafjord.Codes.Where(c => c.Scheme == "knr").Select(c => c.Value).Should().Equal("1940", "5426", "5540");
        kafjord.Codes.Should().Contain(c => c.Value == "5426" && c.From == D("2020-01-01") && c.To == D("2024-01-01"));
        build.Places.Should().NotContainKey("places/no/knr/5426");
        build.Lineage.Values.Should().Contain(e => e.Kind == LineageKind.Recode && e.Date == D("2024-01-01") && e.Operations.Any(o => o.Place == kafjord.Id));
        (await RefAsOf.ResolveAsync(kafjord, D("2025-06-01"), Loader(build))).Places.Should().ContainSingle(p => p.Id == kafjord.Id);
    }

    [Fact]
    public async Task The_county_parent_follows_the_county_codes_own_periods()
    {
        var build = await BuildAsync();
        var kafjord = build.Places["places/no/knr/1940"];
        var troms = build.Places["places/no/fylke/19"];
        var tromsOgFinnmark = build.Places["places/no/fylke/54"];
        var tromsNew = build.Places["places/no/fylke/55"];
        RefAsOf.PeriodAt(kafjord, D("2019-06-01"))!.Parent.Should().Be(troms.Id);
        RefAsOf.PeriodAt(kafjord, D("2021-06-01"))!.Parent.Should().Be(tromsOgFinnmark.Id);
        RefAsOf.PeriodAt(kafjord, D("2025-06-01"))!.Parent.Should().Be(tromsNew.Id);
        RefAsOf.SegmentAt(kafjord, D("2025-06-01"))!.Ancestors.Should().Contain(build.Countries["countries/no"].Place!);
        build.Lineage.Values.Should().Contain(e => e.Kind == LineageKind.Recode && e.Date == D("2020-01-01") && e.Operations.Any(o => o.Place == kafjord.Id && o.Op == LineageOp.Reparent));
    }

    [Fact]
    public async Task A_merge_under_a_new_code_creates_a_new_place_and_a_split_ends_with_part_edges()
    {
        var build = await BuildAsync();
        var larvik = build.Places["places/no/knr/0712"];
        build.Places["places/no/knr/0709"].SupersededBy.Should().ContainSingle(e => e.Id == larvik.Id && e.Share == SuccessionShare.Whole && e.Date == D("2018-01-01"));
        build.Places["places/no/knr/0728"].SupersededBy.Should().ContainSingle(e => e.Id == larvik.Id);
        larvik.Codes.Where(c => c.Scheme == "knr").Select(c => c.Value).Should().Equal("0712", "3805", "3909");

        var tromsOgFinnmark = build.Places["places/no/fylke/54"];
        tromsOgFinnmark.SupersededBy.Should().HaveCount(2);
        tromsOgFinnmark.SupersededBy.Should().OnlyContain(e => e.Share == SuccessionShare.Part && e.Date == D("2024-01-01"));
        (await RefAsOf.ResolveAsync(tromsOgFinnmark, D("2024-06-01"), Loader(build))).Ambiguous.Should().BeTrue();
    }

    [Fact]
    public async Task Sami_and_Kven_names_are_split_per_language()
    {
        var build = await BuildAsync();
        var names = build.Places["places/no/knr/1940"].Names.Entries.Where(e => e.ValidTo is null).ToList();
        names.Should().Contain(e => e.Tag == "se" && e.Value == "Gáivuotna");
        names.Should().Contain(e => e.Tag == "no" && e.Value == "Kåfjord");
        names.Should().Contain(e => e.Tag == "fkv" && e.Value == "Kaivuono");
        // Kåfjord had no Kven name before 2018; its Sami name carries on through every version and code.
        names.Should().Contain(e => e.Tag == "fkv" && e.ValidFrom == D("2018-01-01"));
        names.Should().Contain(e => e.Tag == "se" && e.ValidFrom == null);
        // Oslo gained its South Sami name in 2026; Norwegian is identified through Bring's "OSLO".
        build.Places["places/no/knr/0301"].Names.Entries.Should().Contain(e => e.Tag == "smi" && e.Value == "Oslove" && e.ValidFrom == D("2026-01-01"));
        // Counties are written Norwegian first.
        build.Places["places/no/fylke/55"].Names.Entries.Select(e => e.Tag).Should().Equal("no", "smi", "fkv");
    }

    [Fact]
    public async Task Streets_and_postcodes_come_from_the_address_points_and_Bring()
    {
        var build = await BuildAsync();
        var kafjord = "places/no/knr/1940";
        // Street ids keep the municipality's first code, so a recode never re-identifies them.
        var langnes = build.Streets["streets/no/k1940/1113"];
        langnes.Periods.Should().ContainSingle(p => p.Municipality == kafjord);
        langnes.Periods[0].PostalCodes.Should().Equal("postalcodes/no/9146", "postalcodes/no/9147");
        build.Streets["streets/no/k1940/1108"].Names.Entries.Should().ContainSingle(e => e.Tag == "se" && e.Value == "Olmmáiváhbálggis");
        build.Streets["streets/no/k1940/1103"].Names.Entries.Should().ContainSingle(e => e.Tag == "fkv");
        // Six streets in Kåfjord and one in Kautokeino; the matrikkel address (no street) made none.
        build.Streets.Should().HaveCount(7);

        var olderdalen = build.PostalCodes["postalcodes/no/9146"];
        olderdalen.Localities.Should().ContainSingle(l => l.Key == "olderdalen" && l.Municipalities.Any(m => m.Id == kafjord));
        // Windows-1252 decoded: "TROMSØ" → "Tromsø".
        build.PostalCodes["postalcodes/no/9006"].Localities[0].Names.Entries[0].Value.Should().Be("Tromsø");
    }

    [Fact]
    public async Task Attribution_and_determinism()
    {
        var first = await BuildAsync();
        first.Sources[NorwayAddressSource.SourceId].Attribution.Should().Contain("© Kartverket");
        first.Sources[NorwayKlassSource.SourceId].Attribution.Should().Contain("Statistisk sentralbyrå");
        first.Sources[NorwayPostcodeSource.SourceId].Licence.Should().Be("NLOD 2.0");

        var second = await BuildAsync();
        second.Documents().Select(ReleaseCanonical.Hash).Should().Equal(first.Documents().Select(ReleaseCanonical.Hash));
    }
}
