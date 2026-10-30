using MintPlayer.Spark.Abstractions.Prerendering;

namespace MintPlayer.Spark.Boards.Prerendering;

/// <summary>Server-side rendering of a board page (docs/spark_ssr_PRD.md, SP5).</summary>
public static class SparkBoardsPrerenderExtensions
{
    /// <summary>
    /// Supplies what <c>&lt;spark-board&gt;</c> requests for <paramref name="board"/>: the caller's board,
    /// already filtered to the tiles they may see. A page shows one board under a path of the
    /// application's choosing, so the application maps it:
    /// <code>spark.AddPrerendering(o => o.MapPage("/home", page => page.SupplyBoardAsync("home")));</code>
    /// The board's status becomes the page's.
    /// </summary>
    public static async Task SupplyBoardAsync(this ISparkPrerenderContext context, string board)
    {
        var response = await context.GetAsync($"/spark/boards/{Uri.EscapeDataString(board)}");
        context.SetPageStatus(response.Status);
    }
}
