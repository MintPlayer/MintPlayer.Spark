using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Prerendering;
using MintPlayer.Spark.Boards.Services;

namespace MintPlayer.Spark.Boards.Prerendering;

/// <summary>
/// Server-side rendering of a board page (docs/spark_ssr_PRD.md SP5, D14, D15). A board names the pages that show it
/// (<see cref="SparkBoardDefinition.Paths"/>); on one of them this supplies what <c>&lt;spark-board&gt;</c> requests:
/// the caller's board, already filtered to the tiles they may see, whose status becomes the page's.
/// <para>
/// Up to <see cref="SparkBoardsOptions.PrerenderedTileLimit"/> tiles, each tile's kind also supplies its content,
/// through the same endpoints the tile calls, so security.json and row-level security apply. A larger board renders
/// its tiles as links and loads their content in the browser, which keeps a page's database requests bounded.
/// </para>
/// </summary>
internal sealed partial class SparkBoardsPrerenderContributor : ISparkPrerenderContributor
{
    [Inject] private readonly SparkBoardRegistry registry;
    [Inject] private readonly SparkTileKindRegistry kinds;

    public async Task ContributeAsync(ISparkPrerenderContext context)
    {
        if (registry.FindByPath(context.Path.Value) is not { } board)
            return;

        var response = await context.GetAsync($"/spark/boards/{Uri.EscapeDataString(board.Id)}");
        context.SetPageStatus(response.Status);
        if (response.Read<BoardReference>()?.Tiles is not { Count: > 0 and <= SparkBoardsOptions.PrerenderedTileLimit } tiles)
            return;

        await Task.WhenAll(tiles.Select(tile => kinds.Find(tile.Kind ?? "") is { } kind && tile.Target is { } target
            ? kind.SupplyAsync(context, target)
            : Task.CompletedTask));
    }

    // The members of GET /spark/boards/{board} the supply needs.
    private sealed class BoardReference
    {
        public List<TileReference>? Tiles { get; set; }
    }

    private sealed class TileReference
    {
        public string? Kind { get; set; }
        public Dictionary<string, string>? Target { get; set; }
    }
}
