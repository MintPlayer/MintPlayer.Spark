using MintPlayer.Spark.Commerce.Inventory;

namespace MintPlayer.Spark.Commerce.Tests.Inventory;

/// <summary>Chain fill and the single-seat rule as pure functions (§4.3, D25): no database.</summary>
public class ChainFillTests
{
    private static InventoryChain Chain(params int[] rowLengths) => new()
    {
        Id = "chains/test",
        Rows = rowLengths.Select((length, r) => Enumerable.Range(0, length).Select(s => $"r{r}s{s}").ToList()).ToList(),
    };

    [Fact]
    public void A_group_takes_the_next_run_from_the_cursor()
    {
        var chain = Chain(5, 5);
        var first = ChainFill.Propose(chain, 3, out _)!;
        first.Should().BeEquivalentTo(new[] { 0, 1, 2 });
        ChainFill.Take(chain, first);
        chain.Cursor.Should().Be(3);
        ChainFill.Propose(chain, 2, out _).Should().BeEquivalentTo(new[] { 3, 4 });
    }

    [Fact]
    public void A_row_tail_too_short_is_skipped_and_becomes_a_hole()
    {
        var chain = Chain(5, 5);
        ChainFill.Take(chain, ChainFill.Propose(chain, 3, out _)!);
        var next = ChainFill.Propose(chain, 3, out _)!;
        next.Should().BeEquivalentTo(new[] { 5, 6, 7 });
        ChainFill.Take(chain, next);
        chain.Holes.Should().BeEquivalentTo(new[] { 3, 4 });
        chain.Cursor.Should().Be(8);

        // A pair fits the skipped tail: holes first.
        ChainFill.Propose(chain, 2, out _).Should().BeEquivalentTo(new[] { 3, 4 });
    }

    [Fact]
    public void The_smallest_hole_that_fits_wins()
    {
        var chain = Chain(12);
        foreach (var n in new[] { 2, 1, 3, 1, 2 })
            ChainFill.Take(chain, ChainFill.Propose(chain, n, out _)!);
        // Taken: 0-1, 2, 3-5, 6, 7-8 (cursor 9). Give back 0-1 (a hole of 2) and 3-5 (a hole of 3).
        ChainFill.Return(chain, [0, 1]);
        ChainFill.Return(chain, [3, 4, 5]);
        chain.Cursor.Should().Be(9);
        ChainFill.Propose(chain, 2, out _).Should().BeEquivalentTo(new[] { 0, 1 });
        ChainFill.Propose(chain, 1, out _).Should().BeEquivalentTo(new[] { 0 });
        ChainFill.Propose(chain, 3, out _).Should().BeEquivalentTo(new[] { 3, 4, 5 });
    }

    [Fact]
    public void Returning_the_last_run_rewinds_the_cursor()
    {
        var chain = Chain(6);
        ChainFill.Take(chain, ChainFill.Propose(chain, 2, out _)!);
        var second = ChainFill.Propose(chain, 2, out _)!;
        ChainFill.Take(chain, second);
        ChainFill.Return(chain, second);
        chain.Cursor.Should().Be(2);
        chain.Holes.Should().BeEmpty();
    }

    [Fact]
    public void Free_seats_that_are_not_side_by_side_answer_not_together()
    {
        var chain = Chain(3, 3);
        ChainFill.Take(chain, ChainFill.Propose(chain, 2, out _)!);
        ChainFill.Take(chain, ChainFill.Propose(chain, 2, out _)!); // skips seat 2, takes 3-4
        ChainFill.Propose(chain, 2, out var refusal).Should().BeNull();
        refusal.Should().Be(ClaimStatus.NotTogether);

        ChainFill.Propose(chain, 3, out refusal).Should().BeNull();
        refusal.Should().Be(ClaimStatus.SoldOut);
    }

    [Fact]
    public void A_blocked_seat_is_skipped()
    {
        var chain = Chain(6);
        ChainFill.Block(chain, 1);
        chain.Cursor.Should().Be(2);
        chain.Holes.Should().BeEquivalentTo(new[] { 0 });
        ChainFill.Propose(chain, 3, out _).Should().BeEquivalentTo(new[] { 2, 3, 4 });
    }

    [Fact]
    public void Empty_rows_are_skipped()
    {
        var chain = Chain(2, 0, 3);
        ChainFill.Take(chain, ChainFill.Propose(chain, 2, out _)!);
        var next = ChainFill.Propose(chain, 3, out _)!;
        next.Should().BeEquivalentTo(new[] { 2, 3, 4 });
        ChainFill.UnitAt(chain, ChainFill.RowStarts(chain), 2).Should().Be("r2s0");
    }

    // ---- the single-seat rule ----

    private static readonly string[] Row = ["a", "b", "c", "d", "e"];

    [Fact]
    public void A_pick_leaving_one_seat_between_taken_seats_is_refused()
    {
        // c is taken; picking a leaves b single between a and c.
        SeatGapRules.FindNewSingle(Row, u => u == "c", new HashSet<string> { "a" }, SingleSeatRule.BetweenTaken).Should().Be("b");
        SeatGapRules.FindNewSingle(Row, u => u == "c", new HashSet<string> { "a", "b" }, SingleSeatRule.BetweenTaken).Should().BeNull();
    }

    [Fact]
    public void The_row_end_counts_only_under_the_strict_rule()
    {
        // Picking b leaves a single against the row end.
        SeatGapRules.FindNewSingle(Row, _ => false, new HashSet<string> { "b" }, SingleSeatRule.BetweenTaken).Should().BeNull();
        SeatGapRules.FindNewSingle(Row, _ => false, new HashSet<string> { "b" }, SingleSeatRule.BetweenTakenOrRowEnd).Should().Be("a");
        SeatGapRules.FindNewSingle(Row, _ => false, new HashSet<string> { "b" }, SingleSeatRule.Off).Should().BeNull();
    }

    [Fact]
    public void A_single_that_was_already_there_does_not_refuse()
    {
        // b is already single (a and c taken); picking d and e is fine.
        SeatGapRules.FindNewSingle(Row, u => u is "a" or "c", new HashSet<string> { "d", "e" }, SingleSeatRule.BetweenTakenOrRowEnd).Should().BeNull();
    }
}

public class InventoryLimitKeysTests
{
    [Fact]
    public void Email_addresses_normalise_case_tags_and_gmail_dots()
    {
        InventoryLimitKeys.NormalizeEmail("  John.Doe+tickets@GoogleMail.com ").Should().Be("johndoe@gmail.com");
        InventoryLimitKeys.NormalizeEmail("john.doe+x@example.org").Should().Be("john.doe@example.org");
    }

    [Fact]
    public void Addresses_normalise_diacritics_case_and_punctuation()
        => InventoryLimitKeys.NormalizeAddress("Rue de l'Église 5, 1000 Bruxelles").Should().Be("RUE DE L EGLISE 5 1000 BRUXELLES");

    [Fact]
    public void One_hashed_key_per_identifier_present()
    {
        var lines = InventoryLimitKeys.For("sales/42", max: 4, n: 2, new BuyerIdentifiers(AccountId: "users/1", Email: "A@example.org"));
        lines.Should().HaveCount(2);
        lines.Should().OnlyContain(l => l.Max == 4 && l.N == 2 && l.Key.StartsWith("sales/42/"));
        lines.Should().NotContain(l => l.Key.Contains("example", StringComparison.OrdinalIgnoreCase));
        InventoryLimitKeys.For("sales/42", 4, 2, new BuyerIdentifiers(Email: "a@EXAMPLE.org"))[0].Key
            .Should().Be(lines[1].Key);
    }
}
