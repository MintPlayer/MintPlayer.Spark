using System.Globalization;
using System.Text.RegularExpressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData;
using MintPlayer.Spark.ReferenceData.Build;
using MintPlayer.Spark.ReferenceData.Releases;
using MintPlayer.Spark.ReferenceData.Sources.Switzerland;

namespace ReferenceData.Tests.Adapters;

/// <summary>
/// The Swiss adapter (PRD §4.4, plan R8) on its real fixtures: the BFS commune register snapshot and 2025–2026 mutations
/// (absorb, merge, a cross-canton recode, a boundary adjustment) and swisstopo's street directory.
/// </summary>
public class SwitzerlandAdapterTests
{
    private static Task<RefDataBuild> BuildAsync()
        => new RefDatasetBuilder([new SwitzerlandCommunesSource(), new SwitzerlandStreetsSource()], [new SwitzerlandMapper()])
            .BuildFixturesAsync(CancellationToken.None);

    private static DateOnly D(string text) => DateOnly.Parse(text, CultureInfo.InvariantCulture);

    private static Func<string, Task<RefPlace?>> Loader(RefDataBuild build) => id => Task.FromResult(build.Places.GetValueOrDefault(id));

    [Fact]
    public async Task The_profile_and_the_country_place_are_Swiss()
    {
        var build = await BuildAsync();
        var country = build.Countries["countries/ch"];
        country.Alpha3.Should().Be("CHE");
        country.Profile.TimeZone.Should().Be("Europe/Zurich");
        country.Profile.MunicipalityLevel.Should().Be("ch:municipality");
        Regex.IsMatch("7017", country.Profile.PostcodePattern!).Should().BeTrue();
        Regex.IsMatch("70170", country.Profile.PostcodePattern!).Should().BeFalse();
        country.Place.Should().Be("places/ch/iso3166/CH");
        build.Places["places/ch/iso3166/CH"].Periods.Should().ContainSingle(p => p.Role == PlaceRole.Country);
    }

    [Fact]
    public async Task Cantons_carry_one_name_per_official_language()
    {
        var build = await BuildAsync();
        var bern = build.Places["places/ch/canton/BE"];
        bern.Names.Entries.Should().Contain(e => e.Tag == "de" && e.Value == "Bern");
        bern.Names.Entries.Should().Contain(e => e.Tag == "fr" && e.Value == "Berne");
        build.Places["places/ch/canton/GR"].Names.Entries.Select(e => e.Tag).Should().Equal("de", "it", "rm");
        // Jura became a canton on 1 January 1979; the others predate the register's history (1960).
        build.Places["places/ch/canton/JU"].Lifetime.From.Should().Be(D("1979-01-01"));
        (build.Places["places/ch/canton/ZH"].Lifetime.From is null).Should().BeTrue();
        build.Places["places/ch/canton/ZH"].Codes.Should().Contain(c => c.Scheme == "iso3166-2" && c.Value == "CH-ZH");
    }

    [Fact]
    public async Task A_canton_without_districts_parents_its_communes_directly()
    {
        var build = await BuildAsync();
        var geneve = build.Places["places/ch/bfs/6621"];
        RefAsOf.PeriodAt(geneve, D("2026-01-01"))!.Parent.Should().Be("places/ch/canton/GE");
        build.Places.Keys.Any(k => k.StartsWith("places/ch/district/2500", StringComparison.Ordinal)).Should().BeFalse();
        // Bilingual district names split per language.
        var biel = build.Places["places/ch/bfs/371"];
        RefAsOf.SegmentAt(biel, D("2026-01-01"))!.Ancestors.Should().Contain("places/ch/canton/BE");
    }

    [Fact]
    public async Task Absorb_Chur_receives_Tschiertschen_Praden_2025()
    {
        var build = await BuildAsync();
        var chur = build.Places["places/ch/bfs/3901"];
        var praden = build.Places["places/ch/bfs/3932"];
        build.Lineage.Should().ContainKey("lineage/ch/2025-01-01/absorb/bfs-3901");
        praden.SupersededBy.Should().ContainSingle(e => e.Id == chur.Id && e.Share == SuccessionShare.Whole && e.Date == D("2025-01-01"));
        (chur.Lifetime.From is null).Should().BeTrue();
        chur.Periods.Should().ContainSingle();

        (await RefAsOf.ResolveAsync(praden, D("2024-12-31"), Loader(build))).Places.Should().ContainSingle(p => p.Id == praden.Id);
        (await RefAsOf.ResolveAsync(praden, D("2025-01-01"), Loader(build))).Places.Should().ContainSingle(p => p.Id == chur.Id);
    }

    [Fact]
    public async Task Merge_Grolley_and_Ponthaux_become_Grolley_Ponthaux_2025()
    {
        var build = await BuildAsync();
        var merged = build.Places["places/ch/bfs/2239"];
        build.Lineage.Should().ContainKey("lineage/ch/2025-01-01/merge/bfs-2239");
        merged.Lifetime.From.Should().Be(D("2025-01-01"));
        foreach (var predecessor in new[] { "places/ch/bfs/2200", "places/ch/bfs/2217" })
        {
            build.Places[predecessor].Lifetime.To.Should().Be(D("2025-01-01"));
            build.Places[predecessor].SupersededBy.Should().ContainSingle(e => e.Id == merged.Id);
        }
    }

    [Fact]
    public async Task Recode_Moutier_moves_from_Bern_to_Jura_and_keeps_its_id()
    {
        var build = await BuildAsync();
        build.Places.Should().ContainKey("places/ch/bfs/700");
        build.Places.ContainsKey("places/ch/bfs/6831").Should().BeFalse();
        var moutier = build.Places["places/ch/bfs/700"];
        moutier.Codes.Should().Contain(c => c.Scheme == "bfs" && c.Value == "700" && c.To == D("2026-01-01"));
        moutier.Codes.Should().Contain(c => c.Scheme == "bfs" && c.Value == "6831" && c.From == D("2026-01-01"));
        RefAsOf.SegmentAt(moutier, D("2025-12-31"))!.Ancestors.Should().Contain("places/ch/canton/BE");
        RefAsOf.SegmentAt(moutier, D("2026-01-01"))!.Ancestors.Should().Contain("places/ch/canton/JU");
        var recode = build.Lineage["lineage/ch/2026-01-01/recode/bfs-6831"];
        recode.Operations.Should().Contain(o => o.Op == LineageOp.Recode && o.Place == moutier.Id);
        recode.Operations.Should().Contain(o => o.Op == LineageOp.Reparent && o.Place == moutier.Id);
        // The street directory names the new number; the ledger resolves it to the same place.
        build.Streets["streets/ch/esid/10079358"].Periods.Should().ContainSingle(p => p.Municipality == moutier.Id);
    }

    [Fact]
    public async Task A_boundary_adjustment_gives_no_lineage()
    {
        var build = await BuildAsync();
        build.Lineage.Values.SelectMany(e => e.Operations).Any(o => o.Place == "places/ch/bfs/2404" || o.Place == "places/ch/bfs/2406").Should().BeFalse();
        build.Places["places/ch/bfs/2404"].Periods.Should().ContainSingle();
    }

    [Fact]
    public async Task Streets_split_bilingual_names_and_link_every_postal_locality()
    {
        var build = await BuildAsync();
        var aarberg = build.Streets["streets/ch/esid/10098611"];
        aarberg.Names.Entries.Should().Contain(e => e.Tag == "de" && e.Value == "Aarbergstrasse");
        aarberg.Names.Entries.Should().Contain(e => e.Tag == "fr" && e.Value == "Rue d'Aarberg");
        aarberg.Periods.Single().PostalCodes.Should().Equal("postalcodes/ch/2502", "postalcodes/ch/2503");

        build.Streets["streets/ch/esid/10256480"].Periods.Single().PostalCodes.Should().Equal("postalcodes/ch/7017", "postalcodes/ch/7018");
        build.Streets["streets/ch/esid/10135760"].Type.Should().Be(StreetType.Hamlet);
        // Planned streets are skipped.
        build.Streets.ContainsKey("streets/ch/esid/10251809").Should().BeFalse();
        build.Streets.ContainsKey("streets/ch/esid/10263075").Should().BeFalse();

        var flims = build.PostalCodes["postalcodes/ch/7017"].Localities.Single();
        flims.Names.Entries.Single().Value.Should().Be("Flims Dorf");
        flims.Municipalities.Should().ContainSingle(m => m.Id == "places/ch/bfs/3732");
        build.PostalCodes["postalcodes/ch/1673"].Localities.Select(l => l.Key).Order(StringComparer.Ordinal).Should().Equal("ecublensfr", "gillarens", "promasens", "rue");
    }

    [Fact]
    public async Task Every_response_with_Swiss_data_can_credit_swisstopo()
    {
        var build = await BuildAsync();
        build.Sources[SwitzerlandStreetsSource.SourceId].Attribution.StartsWith("©swisstopo", StringComparison.Ordinal).Should().BeTrue();
        new SwitzerlandStreetsSource().Licence.Compact.StartsWith("©swisstopo", StringComparison.Ordinal).Should().BeTrue();
        build.Sources[SwitzerlandCommunesSource.SourceId].Attribution.Should().Contain("Federal Statistical Office");
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
