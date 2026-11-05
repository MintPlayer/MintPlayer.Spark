using MintPlayer.AspNetCore.OpenSearch.Abstractions;
using MintPlayer.Spark.Search;

namespace MintPlayer.Spark.OpenSearchDescription;

/// <summary>
/// The browser's search engine (<c>docs/spark_search_PRD.md</c> D9): suggestions are Spark search's unambiguous labels,
/// so a picked one resolves back to its object, and a search goes to that object or to the search page (D4). Shares
/// the search endpoints' per-address budget; over it, a suggestion poll gets nothing and a search lands on the page.
/// </summary>
internal sealed class SparkOpenSearchService(
    ISparkSearch search,
    SparkSearchOptions searchOptions,
    SparkOpenSearchDescriptionOptions options,
    SearchRateLimiter rateLimiter,
    IHttpContextAccessor httpContextAccessor) : IOpenSearchService
{
    public async Task<IEnumerable<string>> ProvideSuggestions(string? searchTerms)
    {
        var context = httpContextAccessor.HttpContext;
        if (string.IsNullOrWhiteSpace(searchTerms) || context is null || !rateLimiter.TryAcquire(context))
            return [];

        var hits = await search.SuggestAsync(searchTerms, context.RequestAborted);
        return hits.Select(hit => hit.Label).ToArray();
    }

    public async Task<OpenSearchRedirect> PerformSearch(string? searchTerms)
    {
        var context = httpContextAccessor.HttpContext;
        var pathBase = context?.Request.PathBase.Value ?? "";
        if (string.IsNullOrWhiteSpace(searchTerms))
            return new OpenSearchRedirect(pathBase + searchOptions.SearchPagePath);

        if (options.SingleHit == SingleHitBehavior.Item && context is not null && rateLimiter.TryAcquire(context)
            && await search.ResolveAsync(searchTerms, context.RequestAborted) is { } hit)
            return new OpenSearchRedirect(pathBase + hit.Url);

        return new OpenSearchRedirect($"{pathBase}{searchOptions.SearchPagePath}?q={Uri.EscapeDataString(searchTerms.Trim())}");
    }
}
