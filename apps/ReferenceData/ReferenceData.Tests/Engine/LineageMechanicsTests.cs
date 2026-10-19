using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData;
using MintPlayer.Spark.ReferenceData.Build;
using MintPlayer.Spark.ReferenceData.Sources;
using static ReferenceData.Tests.Engine.RefFixtures;

namespace ReferenceData.Tests.Engine;

/// <summary>
/// The decision-table rows the Belgian fixtures cannot show (PRD §4.2): a split, a cession, a restore and a code
/// reissue, on a hand-built dataset that runs through the same ledger and derive pass. They test the mechanics; the
/// country adapters supply the facts.
/// </summary>
public class LineageMechanicsTests
{
    private static RefDataBuild NewBuild(params LedgerEntry[] ledger)
    {
        var build = new RefDataBuild(new RefLedger(ledger));
        var root = build.Place(RefIds.Place("xx", "iso3166", "XX"));
        root.Periods = [new PlacePeriod { Kind = "xx:country", Role = PlaceRole.Country }];
        root.Names = TranslatedString.Of("en", "Testland");
        return build;
    }

    private static RefPlace Municipality(RefDataBuild build, string code, string name, DateOnly? from, DateOnly? to)
    {
        var place = build.Place(build.Ledger.Mint("test", "code:" + code, RefIds.Place("xx", "code", code)));
        place.Periods.Add(new PlacePeriod { From = from, To = to, Kind = "xx:municipality", Role = PlaceRole.Municipality, Parent = RefIds.Place("xx", "iso3166", "XX") });
        place.Names = TranslatedString.Of("en", name);
        return place;
    }

    [Fact]
    public async Task A_split_with_no_continuing_part_resolves_ambiguous()
    {
        var build = NewBuild();
        var split = Municipality(build, "100", "Old", null, D("2020-01-01"));
        Municipality(build, "200", "East", D("2020-01-01"), null);
        Municipality(build, "300", "West", D("2020-01-01"), null);
        build.AddLineage(new RefLineageEvent
        {
            Id = RefIds.LineageEvent("xx", D("2020-01-01"), LineageKind.Split, "code", "100"),
            Country = "xx", Date = D("2020-01-01"), Kind = LineageKind.Split,
            Operations =
            [
                new LineageOperation { Place = split.Id!, Op = LineageOp.End, Into = RefIds.Place("xx", "code", "200"), Share = SuccessionShare.Part },
                new LineageOperation { Place = split.Id!, Op = LineageOp.End, Into = RefIds.Place("xx", "code", "300"), Share = SuccessionShare.Part },
            ],
        });
        RefDerivation.Derive(build);

        var before = await RefAsOf.ResolveAsync(split, D("2019-12-31"), Loader(build));
        before.Places.Should().ContainSingle();
        before.Ambiguous.Should().BeFalse();

        var after = await RefAsOf.ResolveAsync(split, D("2020-01-01"), Loader(build));
        after.Places.Select(p => p.Id).Should().Equal(RefIds.Place("xx", "code", "200"), RefIds.Place("xx", "code", "300"));
        after.Ambiguous.Should().BeTrue();
    }

    [Fact]
    public async Task A_cession_keeps_the_continuing_unit_and_records_a_part_edge()
    {
        var build = NewBuild();
        var ceding = Municipality(build, "100", "Ceding", null, null);
        var receiving = Municipality(build, "200", "Receiving", null, null);
        build.AddLineage(new RefLineageEvent
        {
            Id = RefIds.LineageEvent("xx", D("1977-01-01"), LineageKind.Cede, "code", "100"),
            Country = "xx", Date = D("1977-01-01"), Kind = LineageKind.Cede,
            Operations = [new LineageOperation { Place = ceding.Id!, Op = LineageOp.Cede, Into = receiving.Id }],
        });
        RefDerivation.Derive(build);

        receiving.Supersedes.Should().ContainSingle(e => e.Id == ceding.Id && e.Share == SuccessionShare.Part);
        var now = await RefAsOf.ResolveAsync(ceding, D("2026-10-19"), Loader(build));
        now.Places.Should().ContainSingle(p => p.Id == ceding.Id);
        now.Ambiguous.Should().BeFalse();
    }

    [Fact]
    public async Task A_restored_unit_keeps_its_id_with_a_new_period()
    {
        // FR: Neussargues en Pinatelle back to five communes, 2025 — the old places get new periods under the same ids.
        var build = NewBuild();
        var restored = Municipality(build, "100", "Restored", null, D("2016-01-01"));
        restored.Periods.Add(new PlacePeriod { From = D("2025-01-01"), Kind = "xx:municipality", Role = PlaceRole.Municipality, Parent = RefIds.Place("xx", "iso3166", "XX") });
        RefDerivation.Derive(build);

        restored.Segments.Should().HaveCount(2);
        restored.Lifetime.Contains(D("2020-06-01")).Should().BeFalse();
        (await RefAsOf.ResolveAsync(restored, D("2026-10-19"), Loader(build))).Places.Should().ContainSingle(p => p.Id == restored.Id);
    }

    [Fact]
    public void A_code_reissued_to_another_unit_needs_a_dated_id_and_conflicts_without_one()
    {
        // INSEE 01015 (2016): the plain id belongs to the earlier unit; a rule that would reuse it is a ledger conflict
        // (RS3: fail closed), and the @yyyy-MM-dd suffix mints a new place.
        var ledger = new RefLedger([new LedgerEntry("insee:01015@1943", RefIds.Place("fr", "insee", "01015"), "fr-cog")]);
        ledger.Mint("fr-cog", "insee:01015@2016", RefIds.Place("fr", "insee", "01015"));
        ledger.Conflicts.Should().ContainSingle();

        var clean = new RefLedger([new LedgerEntry("insee:01015@1943", RefIds.Place("fr", "insee", "01015"), "fr-cog")]);
        var reissued = clean.Mint("fr-cog", "insee:01015@2016", RefIds.Place("fr", "insee", "01015", D("2016-01-01")));
        reissued.Should().Be("places/fr/insee/01015@2016-01-01");
        clean.Conflicts.Should().BeEmpty();
    }
}
