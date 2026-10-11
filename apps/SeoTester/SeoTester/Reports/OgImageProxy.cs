using Microsoft.Extensions.Caching.Memory;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;
using SeoTester.Crawler;
using SeoTester.Entities;
using SeoTester.Fetching;
using SeoTester.Html;

namespace SeoTester.Reports;

/// <summary>
/// The shared-link card's image (<c>docs/seo_analyzer_PRD.md</c> Q-M11d, D-number in §12 "M11 follow-up"):
/// <c>GET /seotester/og-image?page={pageResultId}</c>. The viewer's browser never loads the audited site's
/// <c>og:image</c> itself: a site anyone can add would otherwise learn every viewer's address, and the report would
/// make third-party requests. The server fetches it instead, through the SSRF-safe crawler client
/// (<see cref="ISeoFetcher"/>, the fetcher container in Production, D54: no private addresses, redirects re-checked, the crawler's user agent), and answers the
/// bytes only when:
/// <list type="bullet">
/// <item>the caller may read the page (the <c>Read/PageResult</c> right and the organization row policy; an expired
/// page is gone), else the one 404 (#453);</item>
/// <item>the tag is an absolute http(s) URL without user info;</item>
/// <item>the answer is a 2xx of at most <see cref="OgImageRules.MaxBytes"/> (a larger one is refused, never cut);</item>
/// <item>the bytes are PNG, JPEG, GIF or WebP by their signature (never SVG, never the site's <c>Content-Type</c>).</item>
/// </list>
/// The answer carries <c>nosniff</c> and a sandboxing CSP, and is cached privately by the browser. Results (images and
/// refusals) are kept 10 minutes in a size-capped memory cache, so a page opened many times is fetched once.
/// </summary>
internal sealed partial class OgImageEndpoint : IGetEndpoint
{
    public const string Route = "/seotester/og-image";

    public static string Path => Route;

    /// <summary>At most this many bytes of images are held; an image larger than the whole cache is not cached.</summary>
    private static readonly MemoryCache Cache = new(new MemoryCacheOptions { SizeLimit = 32 * 1024 * 1024 });

    private static readonly TimeSpan Keep = TimeSpan.FromMinutes(10);

    [QueryParam("page")] public string? PageId { get; set; }

    [Inject] private readonly IPermissionService permissions;
    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly ISeoFetcher fetcher;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var ct = httpContext.RequestAborted;
        var pageId = PageId ?? string.Empty;
        if (pageId.Length is 0 or > 300
            || httpContext.User.Identity?.IsAuthenticated != true
            || modelLoader.GetEntityTypeByName(nameof(PageResult)) is not { } type
            || !await permissions.IsAllowedAsync("Read", type.Name, ct))
            return Results.NotFound();

        PersistentObject? page;
        try
        {
            page = await databaseAccess.GetPersistentObjectAsync(type.Id, pageId);
        }
        catch (SparkRowLevelAccessDeniedException)
        {
            page = null;
        }
        if (page is null
            || !page.TryGetAttribute(nameof(PageResult.OgImage), out var attribute)
            || OgImageRules.FetchableUrl(attribute.Value as string) is not { } url)
            return Results.NotFound();

        OgImage? image;
        try
        {
            image = await Cache.GetOrCreateAsync(url.AbsoluteUri, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = Keep;
                var fetched = await FetchAsync(url, ct);
                entry.Size = Math.Max(1, fetched?.Bytes.Length ?? 1);
                return fetched;
            });
        }
        catch (SeoFetcherUnavailableException)
        {
            // Our fetcher is down, not the site's image: nothing cached, the same 404 as any refusal.
            return Results.NotFound();
        }
        if (image is null)
            return Results.NotFound();

        httpContext.Response.Headers.XContentTypeOptions = "nosniff";
        httpContext.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        httpContext.Response.Headers.CacheControl = "private, max-age=3600";
        return Results.Bytes(image.Bytes, image.Type);
    }

    private async Task<OgImage?> FetchAsync(Uri url, CancellationToken ct)
    {
        var result = await fetcher.FetchAsync(url, OgImageRules.MaxBytes, ct);
        if (!result.Ok || result.Truncated || result.Body is not { Length: > 0 } body || OgImageRules.RasterType(body) is not { } type)
            return null;
        return new OgImage(body, type);
    }

    private sealed record OgImage(byte[] Bytes, string Type);
}
