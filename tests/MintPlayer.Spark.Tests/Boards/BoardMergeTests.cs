using MintPlayer.Spark.Boards;
using MintPlayer.Spark.Boards.Services;

namespace MintPlayer.Spark.Tests.Boards;

/// <summary>The pure merge and save rules of <c>docs/dashboard_layout_PRD.md</c> S3 and §4.7.</summary>
public class BoardMergeTests
{
    private static SparkBoardTile Tile(string id) => new() { Id = id, Kind = "query", Target = new() { ["query"] = id } };

    private static SparkTileLayoutEntry At(string id, int col, int row, int colSpan = 1, int rowSpan = 1)
        => new(id, new SparkTilePosition(col, row, colSpan, rowSpan));

    private static SparkBoardDefinition Board() => new()
    {
        Id = "home",
        Tiles = [Tile("a"), Tile("b")],
        Layouts = new() { [2] = [At("a", 1, 1), At("b", 2, 1)] },
    };

    private static readonly HashSet<string> None = [];

    [Fact]
    public void Without_a_stored_layout_the_board_is_the_default()
    {
        var merged = BoardMerge.Merge(Board(), null);

        merged.Tiles.Select(t => t.Tile.Id).Should().Equal("a", "b");
        merged.Tiles.Should().OnlyContain(t => t.IsDefault);
        merged.Layouts[2].Should().Equal(At("a", 1, 1), At("b", 2, 1));
    }

    [Fact]
    public void A_stored_count_replaces_the_default_and_a_removed_default_stays_removed()
    {
        var user = new BoardLayout { DefaultsSeen = ["a", "b"], Removed = ["b"], Layouts = new() { [2] = [At("a", 2, 3)] } };

        var merged = BoardMerge.Merge(Board(), user);

        merged.Tiles.Select(t => t.Tile.Id).Should().Equal("a");
        merged.Layouts[2].Should().Equal(At("a", 2, 3));
    }

    [Fact]
    public void A_default_the_user_has_not_seen_keeps_its_position_where_it_is_free_and_none_where_it_is_not()
    {
        var board = Board();
        board.Tiles.Add(Tile("c"));
        board.Layouts[2].Add(At("c", 1, 2));
        board.Layouts[4] = [At("a", 1, 1), At("c", 3, 1)];
        var user = new BoardLayout
        {
            DefaultsSeen = ["a", "b"],
            Layouts = new() { [2] = [At("a", 1, 1), At("b", 2, 1)], [4] = [At("a", 3, 1)] },
        };

        var merged = BoardMerge.Merge(board, user);

        merged.Layouts[2].Should().Contain(At("c", 1, 2));
        merged.Layouts[4].Select(e => e.Id).Should().Equal("a");
        merged.Tiles.Select(t => t.Tile.Id).Should().Contain("c");
    }

    [Fact]
    public void Filter_drops_the_tiles_and_positions_the_caller_cannot_see()
    {
        var filtered = BoardMerge.Filter(BoardMerge.Merge(Board(), null), new HashSet<string> { "a" });

        filtered.Tiles.Select(t => t.Tile.Id).Should().Equal("a");
        filtered.Layouts[2].Select(e => e.Id).Should().Equal("a");
    }

    [Fact]
    public void A_save_keeps_what_the_caller_could_not_see()
    {
        var stored = new BoardLayout
        {
            DefaultsSeen = ["a", "b"],
            Tiles = [Tile("u-hidden")],
            Titles = new() { ["u-hidden"] = "Mine", ["b"] = "Bee" },
            Layouts = new() { [2] = [At("a", 1, 1), At("b", 2, 1), At("u-hidden", 1, 2)] },
        };
        var request = new BoardMerge.SaveRequest { Layouts = new() { [2] = [At("a", 1, 3)] } };

        var (layout, error) = BoardMerge.Apply(Board(), stored, request, new HashSet<string> { "b", "u-hidden" }, None, 50);

        error.Should().BeNull();
        layout!.Tiles.Select(t => t.Id).Should().Equal("u-hidden");
        layout.Titles.Should().ContainKey("u-hidden");
        layout.Titles.Should().ContainKey("b");
        layout.Layouts[2].Should().Equal(At("a", 1, 3), At("b", 2, 1), At("u-hidden", 1, 2));
    }

    [Theory]
    [InlineData("x", "not a user tile id")]
    [InlineData("u-denied", "cannot access")]
    public void A_user_tile_needs_a_user_id_and_an_accessible_target(string id, string reason)
    {
        var request = new BoardMerge.SaveRequest { Tiles = [Tile(id)] };

        var (layout, error) = BoardMerge.Apply(Board(), null, request, None, None, 50);

        layout.Should().BeNull();
        error.Should().Contain(reason);
    }

    [Fact]
    public void A_save_cannot_place_a_hidden_tile_or_place_outside_the_columns()
    {
        var hidden = new BoardMerge.SaveRequest { Layouts = new() { [2] = [At("b", 1, 1)] } };
        BoardMerge.Apply(Board(), null, hidden, new HashSet<string> { "b" }, None, 50).Error.Should().Contain("not a tile of this board");

        var outside = new BoardMerge.SaveRequest { Layouts = new() { [2] = [At("a", 2, 1, colSpan: 2)] } };
        BoardMerge.Apply(Board(), null, outside, None, None, 50).Error.Should().Contain("outside 2 columns");

        var removeHidden = new BoardMerge.SaveRequest { Removed = ["b"] };
        BoardMerge.Apply(Board(), null, removeHidden, new HashSet<string> { "b" }, None, 50).Error.Should().Contain("not a tile of this board");
    }

    [Fact]
    public void A_save_bounds_the_whole_board_and_the_title_length()
    {
        // The cap counts the live defaults with the user's tiles (D14): 2 defaults + 2 user tiles is over 3.
        var many = new BoardMerge.SaveRequest { Tiles = [Tile("u-1"), Tile("u-2")] };
        BoardMerge.Apply(Board(), null, many, None, new HashSet<string> { "u-1", "u-2" }, 3).Error.Should().Contain("at most 3 tiles");

        // A removed default frees its place.
        var swap = new BoardMerge.SaveRequest { Tiles = [Tile("u-1"), Tile("u-2")], Removed = ["b"] };
        BoardMerge.Apply(Board(), null, swap, None, new HashSet<string> { "u-1", "u-2" }, 3).Error.Should().BeNull();

        var longTitle = new BoardMerge.SaveRequest { Titles = new() { ["a"] = new string('x', BoardMerge.MaxTitleLength + 1) } };
        BoardMerge.Apply(Board(), null, longTitle, None, None, 50).Error.Should().Contain("at most");
    }
}
