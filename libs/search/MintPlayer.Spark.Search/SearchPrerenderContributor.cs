using System.Text;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Prerendering;

namespace MintPlayer.Spark.Search;

/// <summary>
/// Server-side rendering of the SPA's results page (<c>docs/spark_search_PRD.md</c> D11, S8): on
/// <see cref="SparkSearchOptions.SearchPagePath"/> with a <c>?q=</c>, this supplies the <c>GET /spark/search?q=</c>
/// that <c>SparkSearchPageComponent</c> sends, so the page renders with its results and the browser does not fetch them
/// again. The URL must equal the page's byte for byte: the term trimmed and escaped as <c>encodeURIComponent</c> does.
/// </summary>
internal sealed partial class SearchPrerenderContributor : ISparkPrerenderContributor
{
    [Inject] private readonly SparkSearchOptions options;

    public async Task ContributeAsync(ISparkPrerenderContext context)
    {
        if (!string.Equals(context.Path.Value, options.SearchPagePath, StringComparison.OrdinalIgnoreCase))
            return;
        var term = context.HttpContext.Request.Query["q"].ToString().Trim();
        if (term.Length == 0)
            return;

        await context.GetAsync($"/spark/search?q={EncodeUriComponent(term)}");
    }

    /// <summary>JavaScript's <c>encodeURIComponent</c>: <see cref="Uri.EscapeDataString"/> but leaving <c>!'()*</c> as they are.</summary>
    internal static string EncodeUriComponent(string value)
        => new StringBuilder(Uri.EscapeDataString(value))
            .Replace("%21", "!").Replace("%27", "'").Replace("%28", "(").Replace("%29", ")").Replace("%2A", "*")
            .ToString();
}
