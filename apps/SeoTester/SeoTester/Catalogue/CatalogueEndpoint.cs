using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents;
using SeoTester.Entities;
using SeoTester.LookupReferences;
using SeoTester.Reports;
using MintPlayer.AspNetCore.SpaServices.Xsrf;

namespace SeoTester.Catalogue;

/// <summary>
/// <c>GET /seotester/catalogue?culture=en|nl</c>: the check catalogue's texts in one language, for the report pages
/// (<c>docs/seo_analyzer_PRD.md</c> F5, §4.5): each check's title, summary, how-to-fix explanation, platform snippets and
/// docs link, plus the category and severity labels. Anonymous, because the public share page (X6, D31) shows a run's
/// results to anyone holding its link and needs the same texts; the catalogue is the same public reference text for
/// everyone (D32, <c>CheckDefinitionActions.RowSecurityRationale</c>) and holds nothing per user or organization.
/// The texts are the catalogue's current ones (D32), as in the printed report.
/// </summary>
internal sealed partial class CatalogueEndpoint : IGetEndpoint
{
    public const string Route = "/seotester/catalogue";

    public static string Path => Route;

    // FR1: shared-cacheable (Cache-Control: public), so no per-visitor XSRF cookie: the cookie generator would force
    // the response private (SparkMiddleware, UseAntiforgeryGenerator).
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => builder.SkipXsrfToken();

    [QueryParam] public string? Culture { get; set; }

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly MintPlayer.Spark.Services.IRequestCultureResolver cultures;
    [Inject] private readonly MintPlayer.Spark.Services.ITranslationsLoader translations;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var ct = httpContext.RequestAborted;
        var culture = ReportPage.CultureOf(Culture is { Length: > 0 } requested ? requested : cultures.GetCurrentCulture());
        var nl = culture == "nl";

        using var session = store.OpenAsyncSession();
        var definitions = await session.Query<CheckDefinition>().Take(2048).ToListAsync(ct);

        static string? Pick(string? preferred, string? fallback) => string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;
        var checks = definitions
            .OrderBy(d => d.CheckId, StringComparer.Ordinal)
            .Select(d => new CatalogueCheck(
                d.CheckId,
                d.Category,
                d.Severity,
                d.Weight,
                d.Enabled,
                (nl ? Pick(d.TitleNl, d.TitleEn) : d.TitleEn) ?? d.CheckId,
                nl ? Pick(d.SummaryNl, d.SummaryEn) : d.SummaryEn,
                nl ? Pick(d.HowToFixNl, d.HowToFixEn) : d.HowToFixEn,
                Snippets(d),
                d.DocsUrl))
            .ToList();

        httpContext.Response.Headers.CacheControl = "public, max-age=300";
        httpContext.Response.Headers.Vary = "Accept-Language";
        return Results.Json(new
        {
            culture,
            categories = CheckCategoryLookup.Items.Select(i => new { key = i.Key, label = Label(i.Values, culture) }),
            severities = CheckSeverityLookup.Items.Select(i => new { key = i.Key, label = Label(i.Values, culture) }),
            checks,
        });
    }

    /// <summary>A lookup label, which is a <c>translations.json</c> key (commerce X20 item 5), in <paramref name="culture"/>.</summary>
    private string Label(MintPlayer.Spark.Abstractions.TranslatedString label, string culture)
        => label.Key is { } key
            ? MintPlayer.Spark.Abstractions.SparkText.Lookup(translations.GetAll(), key)?.GetValue(culture) ?? MintPlayer.Spark.Abstractions.SparkText.Humanize(key)
            : label.GetValue(culture);

    private static Dictionary<string, string>? Snippets(CheckDefinition d)
    {
        var snippets = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(d.SnippetNginx)) snippets["nginx"] = d.SnippetNginx;
        if (!string.IsNullOrWhiteSpace(d.SnippetApache)) snippets["apache"] = d.SnippetApache;
        if (!string.IsNullOrWhiteSpace(d.SnippetIis)) snippets["iis"] = d.SnippetIis;
        if (!string.IsNullOrWhiteSpace(d.SnippetHtml)) snippets["html"] = d.SnippetHtml;
        return snippets.Count > 0 ? snippets : null;
    }
}

internal sealed record CatalogueCheck(
    string Id,
    string Category,
    string Severity,
    int Weight,
    bool Enabled,
    string Title,
    string? Summary,
    string? HowToFix,
    Dictionary<string, string>? Snippets,
    string? DocsUrl);
