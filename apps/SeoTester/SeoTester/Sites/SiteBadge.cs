using System.Globalization;
using System.Net;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Sharing;
using Raven.Client.Documents;
using SeoTester.Entities;
using MintPlayer.AspNetCore.SpaServices.Xsrf;

namespace SeoTester.Sites;

/// <summary>
/// The site badge (<c>docs/seo_analyzer_PRD.md</c> F11, D31): <c>GET /seotester/badge.svg?site=…&amp;token=…</c>, an SVG
/// with the site's latest score and grade, for a README or a footer. The token is the site's share-link token, so the
/// badge lives and dies with the link: Owners and Admins make it (<see cref="ShareBadgeAction"/>), <c>RevokeShareLink</c>
/// or a new link ends it, the sharing switch (<c>SeoTester:Features:Sharing</c>) hides it. Every refusal is the same 404
/// (#453: no existence oracle).
/// </summary>
internal sealed partial class SiteBadgeEndpoint : IGetEndpoint
{
    public const string Route = "/seotester/badge.svg";

    public static string Path => Route;

    // FR1: shared-cacheable (Cache-Control: public), so no per-visitor XSRF cookie: the cookie generator would force
    // the response private (SparkMiddleware, UseAntiforgeryGenerator).
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => builder.SkipXsrfToken();

    [QueryParam("site")] public string? SiteId { get; set; }
    [QueryParam] public string? Token { get; set; }

    [Inject] private readonly ISparkShareLinks links;
    [Inject] private readonly IDocumentStore store;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var ct = httpContext.RequestAborted;
        var siteId = SiteId ?? string.Empty;
        var token = Token ?? string.Empty;
        if (siteId.Length is 0 or > 200 || token.Length is 0 or > 128 || !await links.IsValidAsync(nameof(Site), siteId, token, ct))
            return Results.NotFound();

        using var session = store.OpenAsyncSession();
        var site = await session.LoadAsync<Site>(siteId, ct);
        if (site is null)
            return Results.NotFound();

        httpContext.Response.Headers.CacheControl = "public, max-age=300";
        return Results.Content(SiteBadgeSvg.Render(site.LatestScore, site.LatestGrade), "image/svg+xml; charset=utf-8");
    }

    /// <summary>The badge's address for the site's link token.</summary>
    public static string UrlFor(string origin, string siteId, string token)
        => $"{origin.TrimEnd('/')}{Route}?site={Uri.EscapeDataString(siteId)}&token={Uri.EscapeDataString(token)}";
}

/// <summary>The badge image: "SEO score" on the left, "87 B" on the right, coloured as the score renderers' letter grades.</summary>
public static class SiteBadgeSvg
{
    public static string Render(int? score, string? grade)
    {
        const string label = "SEO score";
        var value = score is { } s ? $"{s.ToString(CultureInfo.InvariantCulture)} {grade}".Trim() : "not audited";
        // The letter-grade bands of the score renderers (sparkLetterGrades): A, B green; C, D amber; F red.
        var color = grade switch
        {
            "A" or "B" => "#1a7f37",
            "C" or "D" => "#9a6700",
            "F" => "#cf222e",
            _ => "#6e7781",
        };
        static int Width(string text) => 10 + (int)Math.Ceiling(text.Length * 6.6);
        var left = Width(label);
        var right = Width(value);
        var total = left + right;
        var e = WebUtility.HtmlEncode(value);
        return $"""
            <svg xmlns="http://www.w3.org/2000/svg" width="{total}" height="20" role="img" aria-label="{label}: {e}"><title>{label}: {e}</title><linearGradient id="s" x2="0" y2="100%"><stop offset="0" stop-color="#bbb" stop-opacity=".1"/><stop offset="1" stop-opacity=".1"/></linearGradient><clipPath id="r"><rect width="{total}" height="20" rx="3" fill="#fff"/></clipPath><g clip-path="url(#r)"><rect width="{left}" height="20" fill="#555"/><rect x="{left}" width="{right}" height="20" fill="{color}"/><rect width="{total}" height="20" fill="url(#s)"/></g><g fill="#fff" text-anchor="middle" font-family="Verdana,Geneva,DejaVu Sans,sans-serif" font-size="11"><text x="{left / 2.0:0.#}" y="14">{label}</text><text x="{left + right / 2.0:0.#}" y="14">{e}</text></g></svg>
            """;
    }
}

/// <summary>
/// <c>ShareBadge</c> on a site (Owners and Admins, D31): makes a new share link (replacing the old one, as
/// <c>ShareLink</c> does) and shows, once, the Markdown of a badge that links to it.
/// </summary>
public partial class ShareBadgeAction : SparkCustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly ISparkShareLinks links;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken)
    {
        if (args.Parent is not { Id: { Length: > 0 } id })
            throw new SparkValidationException("Open the site first.");
        if (!await links.IsEnabledAsync(nameof(Site), id, cancellationToken))
        {
            manager.Client.Notify(manager.GetTranslatedMessage("share.disabled"), NotificationKind.Error);
            return;
        }

        var token = await links.CreateAsync(nameof(Site), id, cancellationToken);
        var link = links.LinkFor(nameof(Site), id, token, httpContextAccessor.HttpContext);
        // The scheme check keeps a relative link relative on Linux, where TryCreate(Absolute) reads "/x" as file:///x.
        var origin = Uri.TryCreate(link, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https"
            ? absolute.GetLeftPart(UriPartial.Authority)
            : string.Empty;
        var badge = SiteBadgeEndpoint.UrlFor(origin, id, token);
        manager.Client.ShowSecret(
            manager.GetTranslatedMessage("seo.badgeTitle"),
            manager.GetTranslatedMessage("seo.badgeShownOnce"),
            $"[![SEO score]({badge})]({link})");
    }
}
