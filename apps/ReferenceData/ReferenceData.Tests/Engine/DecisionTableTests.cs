using MintPlayer.Spark.ReferenceData;
using static ReferenceData.Tests.Engine.RefFixtures;

namespace ReferenceData.Tests.Engine;

/// <summary>
/// The lineage decision table (PRD §4.2, RS4): one test per row, on the Belgian adapter's real fixtures (Statbel REFNIS,
/// Statbel NIS6, BeST postal streets), each asserting the as-of answer the day before and the day of the change. Rows
/// the Belgian fixtures cannot show (restore, code reissue, a cession) are tested on a hand-built dataset in
/// <see cref="LineageMechanicsTests"/>.
/// </summary>
public class DecisionTableTests
{
    [Fact]
    public async Task Merge_under_a_new_code_Kruisem_2019()
    {
        var build = await BuildAsync();
        var kruisem = build.Places["places/be/nis/45068"];
        var kruishoutem = build.Places["places/be/nis/45017"];
        var zingem = build.Places["places/be/nis/45057"];

        build.Lineage.Should().ContainKey("lineage/be/2019-01-01/merge/nis-45068");
        kruishoutem.SupersededBy.Should().ContainSingle(e => e.Id == kruisem.Id && e.Share == SuccessionShare.Whole && e.Date == D("2019-01-01"));
        zingem.SupersededBy.Should().ContainSingle(e => e.Id == kruisem.Id);

        // The day before: Kruishoutem is its own municipality. The day of: it resolves to Kruisem.
        (await RefAsOf.ResolveAsync(kruishoutem, D("2018-12-31"), Loader(build))).Places.Should().ContainSingle(p => p.Id == kruishoutem.Id);
        (await RefAsOf.ResolveAsync(kruishoutem, D("2019-01-01"), Loader(build))).Places.Should().ContainSingle(p => p.Id == kruisem.Id);

        // The namesake section stays a place of its own (RD3) and re-parents.
        var section = build.Places["places/be/nis6/45017Z"];
        ParentOn(section, "2018-12-31").Should().Be(kruishoutem.Id);
        ParentOn(section, "2019-01-01").Should().Be(kruisem.Id);
    }

    [Fact]
    public async Task One_unit_continues_one_level_down_Ooike_1977()
    {
        var build = await BuildAsync();
        var ooike = build.Places["places/be/nis/45033"];

        // The identity continues, demoted: the municipality of 1976 is the section 45061Z of Wortegem-Petegem.
        ooike.Codes.Should().Contain(c => c.Scheme == "nis6" && c.Value == "45061Z");
        RefAsOf.PeriodAt(ooike, D("1976-12-31"))!.Role.Should().Be(PlaceRole.Municipality);
        RefAsOf.PeriodAt(ooike, D("1977-01-01"))!.Role.Should().Be(PlaceRole.Submunicipality);
        ParentOn(ooike, "1977-01-01").Should().Be("places/be/nis/45061");
        build.Lineage["lineage/be/1977-01-01/absorb/nis-45033"].Operations.Should().Contain(o => o.Op == LineageOp.Demote && o.Place == ooike.Id);
        (await RefAsOf.ResolveAsync(ooike, D("2026-10-19"), Loader(build))).Places.Should().ContainSingle(p => p.Id == ooike.Id);

        // bpost-free: BeST still lists two "Ooike" localities, 9790 (the continuing part) and 9700 (Oudenaarde).
        build.PostalCodes["postalcodes/be/9790"].Localities.Should().Contain(l => l.Key == "ooike" && l.Place == ooike.Id);
        build.PostalCodes["postalcodes/be/9700"].Localities.Should().Contain(l => l.Key == "ooike" && l.Municipalities.Any(m => m.Id == "places/be/nis/45035"));
    }

    [Fact]
    public async Task Same_name_new_code_with_a_joining_unit_is_a_merge_Hasselt_2025()
    {
        // Q-R1: n → 1 under a new code is a merge (Kortessem joined); users still see "Hasselt".
        var build = await BuildAsync();
        var merge = build.Lineage["lineage/be/2025-01-01/merge/nis-71072"];
        merge.Operations.Where(o => o.Op == LineageOp.End).Select(o => o.Place)
            .Should().BeEquivalentTo(new[] { "places/be/nis/71022", "places/be/nis/73040" });
        var old = build.Places["places/be/nis/71022"];
        (await RefAsOf.ResolveAsync(old, D("2024-12-31"), Loader(build))).Places.Single().Id.Should().Be(old.Id);
        (await RefAsOf.ResolveAsync(old, D("2025-01-01"), Loader(build))).Places.Single().Id.Should().Be("places/be/nis/71072");
    }

    [Fact]
    public async Task Merger_across_provinces_reparents_sections_Zwijndrecht_2025()
    {
        // Hierarchy comes from periods, never from code digits: 11056A still starts with province 1's digit.
        var build = await BuildAsync();
        var section = build.Places["places/be/nis6/11056A"];
        AncestorsOn(section, "2024-12-31").Should().Contain("places/be/nis/10000");      // province Antwerpen
        AncestorsOn(section, "2025-01-01").Should().Contain("places/be/nis/40000");      // province Oost-Vlaanderen
        AncestorsOn(section, "2025-01-01").Should().NotContain("places/be/nis/10000");
    }

    [Fact]
    public async Task A_unit_continues_as_a_district_Borsbeek_2025()
    {
        var build = await BuildAsync();
        var borsbeek = build.Places["places/be/nis/11007"];
        RefAsOf.PeriodAt(borsbeek, D("2024-12-31"))!.Kind.Should().Be("be:municipality");
        var after = RefAsOf.PeriodAt(borsbeek, D("2025-01-01"))!;
        after.Kind.Should().Be("be:district");
        after.Parent.Should().Be("places/be/nis/11002");
        borsbeek.SupersededBy.Should().BeEmpty();
    }

    [Fact]
    public async Task Sources_that_disagree_use_the_primary_source_date_Bastogne_2024()
    {
        // Q-R3: Statbel REFNIS (2024-12-01) drives the periods; the decree's 2024-12-02 is not used.
        var build = await BuildAsync();
        var old = build.Places["places/be/nis/82003"];
        old.Lifetime.To.Should().Be(D("2024-12-01"));
        (await RefAsOf.ResolveAsync(old, D("2024-11-30"), Loader(build))).Places.Single().Id.Should().Be(old.Id);
        (await RefAsOf.ResolveAsync(old, D("2024-12-01"), Loader(build))).Places.Single().Id.Should().Be("places/be/nis/82039");
    }

    [Fact]
    public async Task One_code_to_one_code_is_a_recode_La_Louviere_2019()
    {
        var build = await BuildAsync();
        build.Places.Should().NotContainKey("places/be/nis/58001");
        var place = build.Places["places/be/nis/55022"];
        place.Codes.Should().Contain(c => c.Scheme == "nis" && c.Value == "58001" && c.From == D("2019-01-01"));
        place.Codes.Should().Contain(c => c.Scheme == "nis" && c.Value == "55022" && c.To == D("2019-01-01"));
        ParentOn(place, "2018-12-31").Should().Be("places/be/nis/55000");
        ParentOn(place, "2019-01-01").Should().Be("places/be/nis/58000");
        build.Ledger.Find("nis:58001").Should().Be(place.Id);
    }

    [Fact]
    public async Task A_rename_keeps_the_place_and_dates_its_names_Sambreville_1979()
    {
        var build = await BuildAsync();
        var place = build.Places["places/be/nis/92137"];
        PlaceNames.Display(place.Names, ["fr"], "fr", D("1978-06-01")).Display.Should().Be("Basse-Sambre");
        PlaceNames.Display(place.Names, ["fr"], "fr", D("2026-10-19")).Display.Should().Be("Sambreville");
        // The old name stays searchable.
        place.Search.Tokens.Should().Contain("basse");
    }

    [Fact]
    public async Task A_postal_locality_follows_its_section_over_time_9770_Kruishoutem()
    {
        var build = await BuildAsync();
        var locality = build.PostalCodes["postalcodes/be/9770"].Localities.Single(l => l.Key == "kruishoutem");
        locality.Place.Should().Be("places/be/nis6/45017Z");
        locality.Municipalities.Select(m => (m.Id, m.From, m.To)).Should().Equal(
            ("places/be/nis/45017", (DateOnly?)D("1977-01-01"), (DateOnly?)D("2019-01-01")),
            ("places/be/nis/45068", (DateOnly?)D("2019-01-01"), (DateOnly?)null));
    }

    [Fact]
    public async Task Units_absorbed_before_1970_are_places_without_lineage_Bevere()
    {
        // RD4: no hand curation. Bevere (absorbed by Oudenaarde in 1965) exists only as NIS6 45035Y.
        var build = await BuildAsync();
        var bevere = build.Places["places/be/nis6/45035Y"];
        bevere.Supersedes.Should().BeEmpty();
        bevere.SupersededBy.Should().BeEmpty();
        ParentOn(bevere, "2026-10-19").Should().Be("places/be/nis/45035");
    }

    [Fact]
    public async Task Nothing_is_left_unresolved_in_the_seed()
    {
        var build = await BuildAsync();
        build.Lineage.Values.Should().NotContain(e => e.Unresolved);
        build.Warnings.Should().BeEmpty();
    }
}
