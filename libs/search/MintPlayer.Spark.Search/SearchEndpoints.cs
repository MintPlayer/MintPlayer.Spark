using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Prerendering;

namespace MintPlayer.Spark.Search;

/// <summary>
/// <c>GET /spark/search?q=&amp;take=</c>: the results of a term, as the caller may see them (<c>docs/spark_search_PRD.md</c>
/// D10). Anonymous (each hit is still re-read with the caller's rights), rate limited per address, and prerenderable so
/// the SPA's <c>/search</c> page renders on the server.
/// </summary>
internal sealed partial class SearchResultsEndpoint : IGetEndpoint
{
    public static string Path => "/spark/search";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.AllowAnonymous().ExcludeFromDescription().WithMetadata(SparkPrerenderableAttribute.Instance);

    [QueryParam("q")] public string? Q { get; set; }
    [QueryParam("take")] public int? Take { get; set; }

    [Inject] private readonly ISparkSearch search;
    [Inject] private readonly SearchRateLimiter rateLimiter;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        if (!rateLimiter.TryAcquire(httpContext))
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        return Results.Json(await search.SearchAsync(Q, Take, httpContext.RequestAborted));
    }
}

/// <summary><c>GET /spark/search/suggest?q=</c>: suggestions for a term being typed, each label naming one object.</summary>
internal sealed partial class SearchSuggestEndpoint : IGetEndpoint
{
    public static string Path => "/spark/search/suggest";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.AllowAnonymous().ExcludeFromDescription();

    [QueryParam("q")] public string? Q { get; set; }

    [Inject] private readonly ISparkSearch search;
    [Inject] private readonly SearchRateLimiter rateLimiter;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        if (!rateLimiter.TryAcquire(httpContext))
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        return Results.Json(await search.SuggestAsync(Q, httpContext.RequestAborted));
    }
}

/// <summary>
/// <c>GET /spark/search/resolve?q=</c>: the one object a picked suggestion (or a term) names; 404 when it names none or
/// several. The search box uses it when Enter is pressed on text that matches a suggestion (D4).
/// </summary>
internal sealed partial class SearchResolveEndpoint : IGetEndpoint
{
    public static string Path => "/spark/search/resolve";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.AllowAnonymous().ExcludeFromDescription();

    [QueryParam("q")] public string? Q { get; set; }

    [Inject] private readonly ISparkSearch search;
    [Inject] private readonly SearchRateLimiter rateLimiter;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        if (!rateLimiter.TryAcquire(httpContext))
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        return await search.ResolveAsync(Q, httpContext.RequestAborted) is { } hit
            ? Results.Json(hit)
            : Results.NotFound();
    }
}
