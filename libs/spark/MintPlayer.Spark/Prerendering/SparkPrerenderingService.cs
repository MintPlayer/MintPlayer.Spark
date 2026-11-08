using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using MintPlayer.AspNetCore.SpaServices.Prerendering.Services;
using MintPlayer.Spark.Abstractions.Prerendering;
using MintPlayer.Spark.Configuration;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Prerendering;

/// <summary>
/// SpaServices' prerendering hook: supplies the responses a page is going to request, so it renders
/// with its data on the server and hydrates without fetching them again (PRD §3, M3).
/// <para>
/// It hands the render one object, <c>data["spark"]</c> (PRD I11): the responses, the language and the
/// zone. It also sets the page's status from its primary read (D4), or redirects, and marks the page
/// uncacheable (D7).
/// </para>
/// </summary>
internal sealed class SparkPrerenderingService : ISpaPrerenderingService
{
    /// <summary>The cookie ng-spark keeps the chosen language in (spark-language.service.ts).</summary>
    public const string LanguageCookieName = "spark-lang";

    /// <summary>The query parameter that names the page's language (#604): absent for the default language.</summary>
    public const string LanguageQueryName = "lang";

    private readonly SparkPrerenderDispatcher dispatcher;
    private readonly IEnumerable<ISparkPrerenderContributor> contributors;
    private readonly IOptions<SparkPrerenderingOptions> options;
    private readonly IOptions<SparkTimeZoneOptions> timeZoneOptions;
    private readonly ICultureLoader cultureLoader;
    private readonly IRequestCultureResolver cultureResolver;
    private readonly ILogger<SparkPrerenderingService> logger;

    public SparkPrerenderingService(
        SparkPrerenderDispatcher dispatcher,
        IEnumerable<ISparkPrerenderContributor> contributors,
        IOptions<SparkPrerenderingOptions> options,
        IOptions<SparkTimeZoneOptions> timeZoneOptions,
        ICultureLoader cultureLoader,
        IRequestCultureResolver cultureResolver,
        ILogger<SparkPrerenderingService> logger)
    {
        this.logger = logger;
        this.dispatcher = dispatcher;
        this.contributors = contributors;
        this.options = options;
        this.timeZoneOptions = timeZoneOptions;
        this.cultureLoader = cultureLoader;
        this.cultureResolver = cultureResolver;
    }

    public Task BuildRoutes(ISpaRouteBuilder routeBuilder) => Task.CompletedTask;

    public async Task OnSupplyData(HttpContext httpContext, IDictionary<string, object> data)
    {
        // D7: per visitor, never stored. Set first, so a redirect or an error page carries it too.
        httpContext.Response.Headers.CacheControl = "private, no-store";
        httpContext.Response.Headers.Append(HeaderNames.Vary, HeaderNames.Cookie);

        var context = new SparkPrerenderContext(dispatcher, httpContext, ResolveLanguage(httpContext), ResolveTimeZone(httpContext));

        var supplies = contributors.Select(c => c.ContributeAsync(context)).ToList();
        foreach (var (path, supply) in options.Value.Pages)
        {
            if (string.Equals(path.Value, context.Path.Value, StringComparison.OrdinalIgnoreCase))
                supplies.Add(supply(context));
        }
        await Task.WhenAll(supplies);
        logger.LogDebug("Server rendering {Path}: {Requests} requests, {DatabaseRequests} database requests.",
            context.Path, context.Cost.Requests, context.Cost.DatabaseRequests);

        if (context.RedirectTo is { } redirect)
        {
            httpContext.Response.Redirect(redirect.Location, redirect.Permanent);
            return;
        }
        if (context.PageStatus is { } status)
        {
            httpContext.Response.StatusCode = status;
            // D12: a non-200 page is never indexed, whichever component renders its error.
            httpContext.Response.Headers["X-Robots-Tag"] = "noindex";
        }

        data["spark"] = new Dictionary<string, object?>
        {
            ["responses"] = await context.ResponsesAsync(),
            ["language"] = context.Language,
            ["timeZone"] = context.TimeZone,
            // The render URL is relative (PRD I4), so the page learns its public origin here (D12: canonical, og:url).
            ["origin"] = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}{httpContext.Request.PathBase}",
        };
    }

    /// <summary>
    /// The page's language (#604 D1): the URL's <c>?lang</c>, then the <c>spark-lang</c> cookie, each only when it names a
    /// configured language, else what an API request would resolve (<c>Accept-Language</c>, then the default). The URL
    /// wins so a shared or crawled <c>?lang=nl</c> link renders Dutch whatever the visitor last picked.
    /// </summary>
    internal string ResolveLanguage(HttpContext httpContext)
        => ResolveLanguage(httpContext, cultureLoader.GetCulture().Languages.Keys) ?? cultureResolver.GetCurrentCulture();

    /// <summary><c>?lang</c>, then the cookie, when configured; null otherwise. Case as configured.</summary>
    internal static string? ResolveLanguage(HttpContext httpContext, IEnumerable<string> languages)
    {
        var configured = languages.ToList();
        string? Known(string? code) => string.IsNullOrEmpty(code) ? null
            : configured.FirstOrDefault(l => string.Equals(l, code, StringComparison.OrdinalIgnoreCase));
        return Known(httpContext.Request.Query[LanguageQueryName].FirstOrDefault())
            ?? Known(httpContext.Request.Cookies[LanguageCookieName]);
    }

    /// <summary>The timezone cookie when it names a known zone, else the application's default (D10).</summary>
    private string ResolveTimeZone(HttpContext httpContext)
    {
        var cookieName = timeZoneOptions.Value.CookieName;
        var cookie = string.IsNullOrWhiteSpace(cookieName) ? null : httpContext.Request.Cookies[cookieName];
        if (RequestTimeZoneResolver.IsWellFormedZoneId(cookie) && TimeZoneInfo.TryFindSystemTimeZoneById(cookie!, out _))
            return cookie!;
        return options.Value.DefaultTimeZone;
    }
}
