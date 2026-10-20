using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Models;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Plans;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Services;

namespace MintPlayer.Spark.Commerce.Tests.Venues;

/// <summary>
/// The venues package's pure parts (M14, PRD §4.11, D7/D25/D32, default 3): plan validation, CSV, the snapshot's
/// inventory mapping (chains, quotas, picks), accessible seating, the visual diff and the packed seat map. No database.
/// </summary>
public class LayoutPlanTests
{
    /// <summary>Block A: 2 rows × 4 seats, A-1 accessible with A-2 its companion; block P: a standing pit of 300.</summary>
    internal static LayoutPlan Plan()
    {
        LayoutRow Row(string label, int y) => new()
        {
            Key = label,
            Label = label,
            Seats = Enumerable.Range(1, 4).Select(s => new LayoutSeat { Key = $"A-{label}-{s}", Label = s.ToString(), X = s * 10, Y = y }).ToList(),
        };
        var plan = new LayoutPlan
        {
            Width = 200,
            Height = 200,
            Categories = [new LayoutCategory { Key = "cat1", Name = "Cat 1", Color = "#ff0000" }],
            Blocks =
            [
                new LayoutBlock { Key = "A", Name = "Block A", Kind = BlockKind.SeatedNumbered, Polygon = [0, 0, 60, 0, 60, 40, 0, 40], Category = "cat1", Rows = [Row("1", 10), Row("2", 20)] },
                new LayoutBlock { Key = "P", Name = "Pit", Kind = BlockKind.Standing, Polygon = [100, 0, 150, 0, 150, 50], Capacity = 300 },
            ],
            Chains = [new LayoutChain { Key = "main", Name = "Main", Blocks = [new LayoutChainBlock { BlockKey = "A" }] }],
        };
        plan.Blocks[0].Rows[0].Seats[0].Tags = SeatTags.Accessible;
        plan.Blocks[0].Rows[0].Seats[1].Tags = SeatTags.Companion;
        return plan;
    }

    // ---- validation ----

    [Fact]
    public void A_good_plan_has_no_problems()
        => LayoutPlanValidator.Validate(Plan()).Should().BeEmpty();

    [Fact]
    public void Duplicate_seat_keys_bad_keys_and_dangling_references_are_problems()
    {
        var plan = Plan();
        plan.Blocks[0].Rows[1].Seats[0].Key = "A-1-1"; // duplicates row 1's first seat
        plan.Blocks[0].Rows[1].Seats[1].Key = "has/slash";
        plan.Blocks[0].Rows[1].Seats[2].Category = "nope";
        plan.Chains.Add(new LayoutChain { Key = "pit", Name = "Pit", Blocks = [new LayoutChainBlock { BlockKey = "P" }] });
        plan.Chains.Add(new LayoutChain { Key = "again", Name = "Again", Blocks = [new LayoutChainBlock { BlockKey = "A" }] });
        plan.Blocks[1].Capacity = 0;

        var codes = LayoutPlanValidator.Validate(plan).Select(p => p.Code).ToList();
        codes.Should().Contain("ticketingVenues.problem.key");
        codes.Should().Contain("ticketingVenues.problem.category");
        codes.Should().Contain("ticketingVenues.problem.chainBlock");
        codes.Should().Contain("ticketingVenues.problem.chainedTwice");
        codes.Should().Contain("ticketingVenues.problem.capacity");
    }

    [Fact]
    public void A_companion_naming_a_seat_in_another_row_is_a_problem()
    {
        var plan = Plan();
        plan.Blocks[0].Rows[0].Seats[1].CompanionOf = "A-2-1";
        LayoutPlanValidator.Validate(plan).Select(p => p.Code).Should().Contain("ticketingVenues.problem.companion");
    }

    [Fact]
    public void Too_many_seats_is_a_problem()
        => LayoutPlanValidator.Validate(Plan(), new LayoutPlanLimits(MaxSeats: 7)).Select(p => p.Code).Should().Contain("ticketingVenues.problem.tooManySeats");

    // ---- CSV (D32) ----

    [Fact]
    public void Csv_round_trips_seats_tags_and_categories()
    {
        var plan = Plan();
        plan.Blocks[0].Rows[1].Seats[3].Category = "cat1";
        var csv = LayoutCsv.Export(plan);
        var emptied = Plan();
        emptied.Blocks[0].Rows.Clear();

        var (imported, problems) = LayoutCsv.Import(emptied, csv);
        problems.Should().BeEmpty();
        var block = imported!.Blocks.Single(b => b.Key == "A");
        block.Rows.Select(r => r.Label).Should().Equal("1", "2");
        block.Rows[0].Seats[0].Tags.Should().Be(SeatTags.Accessible);
        block.Rows[1].Seats[3].Category.Should().Be("cat1");
        imported.Blocks.Single(b => b.Key == "P").Capacity.Should().Be(300);
        LayoutPlanValidator.Validate(imported).Should().BeEmpty();
    }

    [Fact]
    public void Csv_export_guards_formula_injection_and_import_undoes_it()
    {
        var plan = Plan();
        plan.Blocks[0].Rows[0].Label = "=cmd|' /C calc'!A0";
        var csv = LayoutCsv.Export(plan);
        csv.Should().Contain("'=cmd|' /C calc'!A0");
        var (imported, _) = LayoutCsv.Import(plan, csv);
        imported!.Blocks[0].Rows[0].Label.Should().Be("=cmd|' /C calc'!A0");
    }

    [Fact]
    public void Csv_import_reports_bad_lines_and_never_writes_into_a_standing_block()
    {
        LayoutCsv.Import(Plan(), "block,row,seat,x,y\r\nA,1,1,x,1\r\n").Problems.Single().Should().Be(new LayoutProblem("line 2", "ticketingVenues.csv.position"));
        LayoutCsv.Import(Plan(), "block,row,seat,x,y\r\nP,1,1,1,1\r\n").Problems.Single().Code.Should().Be("ticketingVenues.csv.notNumbered");
        LayoutCsv.Import(Plan(), "nonsense").Problems.Single().Code.Should().Be("ticketingVenues.csv.header");
        LayoutCsv.Parse("a,\"b,\"\"c\"\"\",d\r\n").Single().Should().Equal("a", "b,\"c\"", "d");
    }

    // ---- the snapshot's inventory mapping (D7, D25) ----

    [Fact]
    public void Chains_follow_the_direction_and_skip_accessible_and_companion_seats()
    {
        var plan = Plan();
        var map = new SnapshotInventory("VenueLayoutSnapshots/s1", plan);
        var rows = map.ChainRows(plan.Chains[0]);
        rows.Should().HaveCount(2);
        rows[0].Should().Equal("VenueLayoutSnapshots/s1/A-1-3", "VenueLayoutSnapshots/s1/A-1-4");
        rows[1].Should().Equal("VenueLayoutSnapshots/s1/A-2-1", "VenueLayoutSnapshots/s1/A-2-2", "VenueLayoutSnapshots/s1/A-2-3", "VenueLayoutSnapshots/s1/A-2-4");

        plan.Chains[0].Blocks[0].BackToFront = true;
        plan.Chains[0].Blocks[0].ReverseSeats = true;
        map.ChainRows(plan.Chains[0])[0].Should().Equal("VenueLayoutSnapshots/s1/A-2-4", "VenueLayoutSnapshots/s1/A-2-3", "VenueLayoutSnapshots/s1/A-2-2", "VenueLayoutSnapshots/s1/A-2-1");
    }

    [Fact]
    public void Picks_carry_the_whole_row_and_quotas_name_the_block()
    {
        var map = new SnapshotInventory("VenueLayoutSnapshots/s1", Plan());
        var picks = map.Picks(["A-2-2", "A-2-3", "A-1-4"], SingleSeatRule.BetweenTaken);
        picks.Should().HaveCount(2);
        var row2 = picks.Single(p => p.Picked.Count == 2);
        row2.RowUnits.Should().HaveCount(4);
        row2.Rule.Should().Be(SingleSeatRule.BetweenTaken);
        map.Places("P", 3).Should().Be(new QuotaLine("VenueLayoutSnapshots/s1/blocks/P", 3));
        map.BlockQuotas().Should().Equal(("VenueLayoutSnapshots/s1/blocks/P", 300));
        map.SeatKeyOf("VenueLayoutSnapshots/s1/A-1-1").Should().Be("A-1-1");
        map.SeatKeyOf("VenueLayoutSnapshots/other/A-1-1").Should().BeNull();
        Action unknown = () => map.Picks(["nope"]);
        unknown.Should().Throw<ArgumentException>();
    }

    // ---- accessible seating (default 3) ----

    [Fact]
    public void A_wheelchair_space_needs_a_ticket_type_that_allows_it_and_its_companion_travels_with_it()
    {
        var plan = Plan();
        AccessibleSeating.Check(plan, ["A-1-1"], allowedTags: 0).Single().Refusal.Should().Be(SeatRefusal.AccessibleOnly);
        AccessibleSeating.Check(plan, ["A-1-1"], SeatTags.Accessible).Should().BeEmpty();
        AccessibleSeating.Check(plan, ["A-1-2"], allowedTags: 0).Single().Refusal.Should().Be(SeatRefusal.CompanionOnly);
        AccessibleSeating.Check(plan, ["A-1-1", "A-1-2"], SeatTags.Accessible).Should().BeEmpty();
        AccessibleSeating.Check(plan, ["A-1-3", "ghost"], 0).Single().Refusal.Should().Be(SeatRefusal.Unknown);
        AccessibleSeating.CompanionsOf(plan, "A-1-1").Should().Equal("A-1-2");
        AccessibleSeating.CompanionsOf(plan, "A-1-3").Should().BeEmpty();
    }

    // ---- diff and packing ----

    [Fact]
    public void The_diff_counts_added_removed_moved_and_retagged_seats()
    {
        var before = Plan();
        var after = Plan();
        after.Blocks[0].Rows[0].Seats.RemoveAt(3);                     // A-1-4 removed
        after.Blocks[0].Rows[1].Seats[0].X += 5;                       // A-2-1 moved
        after.Blocks[0].Rows[1].Seats[1].Tags = SeatTags.RestrictedView; // A-2-2 retagged
        after.Blocks[0].Rows[1].Seats.Add(new LayoutSeat { Key = "A-2-5", Label = "5", X = 50, Y = 20 });
        after.Chains.Clear();

        var diff = LayoutDiff.Compare(before, after);
        diff.Added.Should().Equal("A-2-5");
        diff.Removed.Should().Equal("A-1-4");
        diff.Moved.Should().Equal("A-2-1");
        diff.Changed.Should().Equal("A-2-2");
        diff.ChangedChains.Should().Equal("main");
        LayoutDiff.Compare(null, after).AddedCount.Should().Be(8);
    }

    [Fact]
    public void The_seat_map_packs_seats_in_map_order_and_states_in_two_bits()
    {
        var payload = SeatMapPacking.Payload("VenueLayoutSnapshots/s1", "Hall", Plan());
        payload.Seats.Keys.Should().HaveCount(8);
        payload.Blocks[0].SeatStart.Should().Be(0);
        payload.Blocks[0].SeatCount.Should().Be(8);
        payload.Blocks[1].Capacity.Should().Be(300);
        payload.Seats.Category.All(c => c == 0).Should().BeTrue();
        payload.Rows.SeatStart.Should().Equal(0, 4);
        new SnapshotInventory("VenueLayoutSnapshots/s1", Plan()).UnitsInMapOrder().Select(u => u.Split('/')[^1]).Should().Equal(payload.Seats.Keys);

        var states = new[] { UnitState.Free, UnitState.Held, UnitState.Sold, UnitState.Free, UnitState.Sold };
        var packed = SeatMapPacking.PackStates(states);
        packed.Should().HaveCount(2);
        Enumerable.Range(0, states.Length).Select(i => SeatMapPacking.UnpackState(packed, i)).Should().Equal(states);
    }

    [Fact]
    public void Background_images_are_judged_by_their_bytes()
    {
        SparkVenues.SniffImage([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0]).Should().Be("image/png");
        SparkVenues.SniffImage([0xFF, 0xD8, 0xFF, 0xE0]).Should().Be("image/jpeg");
        SparkVenues.SniffImage("RIFF\0\0\0\0WEBPVP8 "u8).Should().Be("image/webp");
        SparkVenues.SniffImage("<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8).Should().BeNull();
    }

    [Fact]
    public void A_scope_maps_to_one_short_snapshot_id()
    {
        var id = SparkVenues.SnapshotId("EventDates/42-A");
        id.Should().StartWith("VenueLayoutSnapshots/");
        id.Should().Be(SparkVenues.SnapshotId("EventDates/42-A"));
        id.Should().NotBe(SparkVenues.SnapshotId("EventDates/43-A"));
        id.Length.Should().BeLessThan(50);
    }
}
