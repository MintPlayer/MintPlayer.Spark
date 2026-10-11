using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using SeoTester.Crawling;
using SeoTester.Html;
using SeoTester.Scoring;

namespace SeoTester.Checks;

/// <summary>
/// The checks measured on every crawled HTML page from its raw HTML and its response headers (catalogue layers H and
/// N, <c>docs/seo_analyzer_PRD.md</c> §3.1, research §1). Each page gives one finding per check; the aggregate turns
/// them into a pass ratio across pages (§7). Pure: no I/O, so every check is a unit test away.
/// Checks of a rendered page (layer R only) and of PageSpeed Insights wait for M10 (<see cref="CheckImplementations"/>).
/// </summary>
public static partial class PageChecks
{
    /// <summary>The check ids this class can emit.</summary>
    public static IReadOnlyList<string> Ids { get; } =
    [
        "seo.title-present", "seo.title-length", "seo.meta-description", "seo.single-h1", "seo.canonical", "seo.hreflang", "seo.html-lang",
        "seo.content-language-header", "seo.noindex-conflict", "seo.structured-data", "seo.microdata-rdfa", "seo.link-text", "seo.thin-content",
        "seo.url-hygiene", "seo.meta-keywords", "seo.sitelinks-searchbox", "seo.deprecated-rich-results",
        "social.open-graph", "social.twitter-card", "social.share-buttons",
        "performance.compression", "performance.validators", "performance.media-dimensions", "performance.resource-hints", "performance.inline-styles",
        "security.hsts", "security.mixed-content", "security.csp", "security.nosniff", "security.clickjacking", "security.referrer-policy",
        "security.permissions-policy", "security.cross-origin-isolation", "security.server-signature", "security.x-xss-protection",
        "security.obsolete-headers", "security.sri", "security.target-blank-opener", "security.csrf-heuristic", "security.cookie-flags",
        "accessibility.img-alt", "accessibility.form-labels",
        "mobile.viewport", "mobile.zoom-disabled", "mobile.charset", "mobile.theme-color", "mobile.apple-web-app", "mobile.amp",
        "technical.html-validity",
    ];

    /// <summary>Every page-level finding for <paramref name="page"/>. A page that is not a 200 HTML page gets none (the crawl-level checks count it).</summary>
    public static IReadOnlyList<CheckFinding> Evaluate(PageInput page)
    {
        if (page.StatusCode != 200 || !page.IsHtml || page.Html is null)
            return [];
        var html = page.Html;
        var url = page.Url.AbsoluteUri;
        var findings = new List<CheckFinding>(Ids.Count);
        void Add(CheckFinding finding) => findings.Add(finding);

        // ---- SEO -------------------------------------------------------------------------------------------------
        var titles = html.Titles;
        var title = html.Title;
        Add(titles.Count == 0 || string.IsNullOrEmpty(title) ? Finding.Fail("seo.title-present", ("url", url))
            : titles.Count > 1 ? Finding.Warn("seo.title-present", ("url", url), ("n", titles.Count))
            : Finding.Pass("seo.title-present"));

        if (string.IsNullOrEmpty(title))
            Add(Finding.NotApplicable("seo.title-length"));
        else
        {
            var pixels = TitleWidth.Pixels(title);
            Add(title.Length is < 30 or > 60 || pixels > TitleWidth.TruncationPx
                ? Finding.Warn("seo.title-length", ("url", url), ("n", title.Length), ("value", Math.Round(pixels)))
                : Finding.Pass("seo.title-length", ("n", title.Length)));
        }

        var description = html.Description;
        Add(string.IsNullOrEmpty(description) ? Finding.Fail("seo.meta-description", ("url", url), ("n", 0))
            : description.Length is < 70 or > 160 ? Finding.Warn("seo.meta-description", ("url", url), ("n", description.Length))
            : Finding.Pass("seo.meta-description"));

        var h1s = html.H1s;
        Add(h1s.Count == 1 ? Finding.Pass("seo.single-h1")
            : h1s.Count == 0 ? Finding.Fail("seo.single-h1", ("url", url), ("n", 0))
            : Finding.Warn("seo.single-h1", ("url", url), ("n", h1s.Count)));

        Add(Canonical(page, html, url));
        Add(Hreflang(page, html, url));

        var lang = html.Document.DocumentElement.GetAttribute("lang");
        Add(string.IsNullOrWhiteSpace(lang) ? Finding.Fail("seo.html-lang", ("url", url))
            : !IsLanguageTag(lang) ? Finding.Fail("seo.html-lang", ("url", url), ("value", lang))
            : Finding.Pass("seo.html-lang", ("value", lang)));

        Add(page.Header("content-language") is { } contentLanguage
            ? Finding.Warn("seo.content-language-header", ("value", contentLanguage))
            : Finding.Pass("seo.content-language-header"));

        Add(!page.NoIndex ? Finding.Pass("seo.noindex-conflict")
            : page.InSitemap || page.LinkedInternally ? Finding.Fail("seo.noindex-conflict", ("url", url), ("value", page.InSitemap ? "sitemap" : "link"))
            : Finding.NotApplicable("seo.noindex-conflict"));

        var jsonLd = JsonLd(html);
        Add(StructuredData(jsonLd, url));
        Add(html.Document.QuerySelector("[itemscope], [typeof], [vocab]") is not null
            ? Finding.Warn("seo.microdata-rdfa", ("url", url)) : Finding.Pass("seo.microdata-rdfa"));
        Add(LinkText(html, url));

        var words = html.MainWordCount;
        Add(words < 200 ? Finding.Warn("seo.thin-content", ("url", url), ("n", words)) : Finding.Pass("seo.thin-content", ("n", words)));
        Add(UrlProblem(page.Url) is { } urlProblem ? Finding.Warn("seo.url-hygiene", ("n", 1), ("target", url), ("value", urlProblem)) : Finding.Pass("seo.url-hygiene"));
        Add(html.Meta("keywords") is not null ? Finding.Warn("seo.meta-keywords", ("url", url)) : Finding.Pass("seo.meta-keywords"));

        var types = jsonLd.SelectMany(j => j.Types).ToList();
        Add(jsonLd.Any(j => j.HasSearchAction) ? Finding.Warn("seo.sitelinks-searchbox", ("url", url)) : Finding.Pass("seo.sitelinks-searchbox"));
        Add(types.FirstOrDefault(t => DeprecatedRichResultTypes.Contains(t)) is { } deprecated
            ? Finding.Warn("seo.deprecated-rich-results", ("url", url), ("value", deprecated))
            : Finding.Pass("seo.deprecated-rich-results"));

        // ---- Social ----------------------------------------------------------------------------------------------
        var missingOg = new[] { "og:title", "og:description", "og:image", "og:url", "og:type" }.Where(p => string.IsNullOrWhiteSpace(html.MetaProperty(p))).ToList();
        var ogImage = html.MetaProperty("og:image");
        if (ogImage is not null && !IsHttpUrl(ogImage))
            missingOg.Add("og:image (not an absolute URL)");
        Add(missingOg.Count == 0 ? Finding.Pass("social.open-graph")
            : missingOg.Contains("og:title") || missingOg.Contains("og:image") ? Finding.Fail("social.open-graph", ("url", url), ("value", string.Join(", ", missingOg)))
            : Finding.Warn("social.open-graph", ("url", url), ("value", string.Join(", ", missingOg))));
        Add(string.IsNullOrWhiteSpace(html.MetaProperty("twitter:card")) ? Finding.Warn("social.twitter-card", ("url", url)) : Finding.Pass("social.twitter-card"));
        var share = ShareTargets(html);
        Add(share.Count > 0 ? Finding.Warn("social.share-buttons", ("url", url), ("value", string.Join(", ", share))) : Finding.Pass("social.share-buttons"));

        // ---- Performance -----------------------------------------------------------------------------------------
        Add(page.BodyBytes < 1024 ? Finding.NotApplicable("performance.compression")
            : IsCompressed(page.WireEncoding) ? Finding.Pass("performance.compression", ("value", page.WireEncoding))
            : Finding.Fail("performance.compression", ("n", 1), ("target", url)));
        Add(page.Header("etag") is null && page.Header("last-modified") is null
            ? Finding.Warn("performance.validators", ("url", url)) : Finding.Pass("performance.validators"));
        var undimensioned = html.Document.QuerySelectorAll("img, video, iframe").Count(e =>
            !(e.HasAttribute("width") && e.HasAttribute("height")) && !(e.GetAttribute("style") ?? string.Empty).Contains("aspect-ratio", StringComparison.OrdinalIgnoreCase)
            && !(e.GetAttribute("style") is { } style && style.Contains("width", StringComparison.OrdinalIgnoreCase) && style.Contains("height", StringComparison.OrdinalIgnoreCase)));
        Add(undimensioned == 0 ? Finding.Pass("performance.media-dimensions")
            : Finding.Make("performance.media-dimensions", undimensioned >= 3 ? CheckStatus.Fail : CheckStatus.Warn, [("url", url), ("n", undimensioned)]));
        var preconnects = html.Links("preconnect").Count();
        Add(preconnects > 4 ? Finding.Warn("performance.resource-hints", ("url", url), ("n", preconnects)) : Finding.Pass("performance.resource-hints", ("n", preconnects)));
        var inlineStyles = html.Document.QuerySelectorAll("[style]").Length;
        Add(inlineStyles > 20 ? Finding.Warn("performance.inline-styles", ("url", url), ("n", inlineStyles)) : Finding.Pass("performance.inline-styles", ("n", inlineStyles)));

        // ---- Security --------------------------------------------------------------------------------------------
        Add(Hsts(page));
        Add(MixedContent(page, html, url));
        var csp = page.Header("content-security-policy") ?? html.HttpEquiv("Content-Security-Policy");
        Add(csp is null ? Finding.Fail("security.csp", ("value", "no policy"))
            : CspWeakness(csp) is { } weakness ? Finding.Warn("security.csp", ("value", weakness))
            : Finding.Pass("security.csp"));
        Add(string.Equals(page.Header("x-content-type-options")?.Trim(), "nosniff", StringComparison.OrdinalIgnoreCase)
            ? Finding.Pass("security.nosniff") : Finding.Fail("security.nosniff", ("n", 1), ("target", url)));
        var xfo = page.Header("x-frame-options");
        var frameAncestors = page.Header("content-security-policy") is { } headerCsp && headerCsp.Contains("frame-ancestors", StringComparison.OrdinalIgnoreCase);
        Add(frameAncestors || xfo is { } x && (x.Contains("deny", StringComparison.OrdinalIgnoreCase) || x.Contains("sameorigin", StringComparison.OrdinalIgnoreCase))
            ? Finding.Pass("security.clickjacking") : Finding.Fail("security.clickjacking", ("url", url)));
        var referrer = page.Header("referrer-policy") ?? html.Meta("referrer");
        Add(referrer is { } r && (r.Contains("unsafe-url", StringComparison.OrdinalIgnoreCase) || r.Contains("no-referrer-when-downgrade", StringComparison.OrdinalIgnoreCase))
            ? Finding.Warn("security.referrer-policy", ("value", r)) : Finding.Pass("security.referrer-policy", ("value", referrer)));
        Add(page.Header("permissions-policy") is null ? Finding.Warn("security.permissions-policy") : Finding.Pass("security.permissions-policy"));
        var isolation = new[] { "cross-origin-opener-policy", "cross-origin-embedder-policy", "cross-origin-resource-policy" }
            .Where(h => page.Header(h) is not null).Select(h => $"{h}: {page.Header(h)}").ToList();
        Add(isolation.Count > 0 ? Finding.Pass("security.cross-origin-isolation", ("value", string.Join("; ", isolation)))
            : Finding.Warn("security.cross-origin-isolation", ("value", "none")));
        Add(ServerSignature(page) is { } signature ? Finding.Warn("security.server-signature", ("value", signature)) : Finding.Pass("security.server-signature"));
        Add(page.Header("x-xss-protection") is { } xss && xss.Trim() != "0"
            ? Finding.Warn("security.x-xss-protection", ("value", xss)) : Finding.Pass("security.x-xss-protection"));
        var obsolete = new[] { "expect-ct", "public-key-pins", "public-key-pins-report-only", "feature-policy", "p3p" }.Where(h => page.Header(h) is not null).ToList();
        Add(obsolete.Count > 0 ? Finding.Warn("security.obsolete-headers", ("value", string.Join(", ", obsolete))) : Finding.Pass("security.obsolete-headers"));
        Add(Sri(page, html, url));
        var opener = html.Document.QuerySelectorAll("a[rel]").Count(a => HtmlPage.HasToken(a.GetAttribute("rel"), "opener"));
        Add(opener > 0 ? Finding.Warn("security.target-blank-opener", ("url", url), ("n", opener)) : Finding.Pass("security.target-blank-opener"));
        Add(Csrf(html, url));
        Add(CookieFlags(page));

        // ---- Accessibility (the raw-HTML part; axe on the rendered page is M10) ---------------------------------
        var images = html.Document.QuerySelectorAll("img");
        var noAlt = images.Count(i => !i.HasAttribute("alt"));
        Add(images.Length == 0 ? Finding.NotApplicable("accessibility.img-alt")
            : noAlt > 0 ? Finding.Fail("accessibility.img-alt", ("url", url), ("n", noAlt)) : Finding.Pass("accessibility.img-alt"));
        Add(FormLabels(html, url));

        // ---- Mobile ----------------------------------------------------------------------------------------------
        var viewport = html.Meta("viewport");
        Add(viewport is null ? Finding.Fail("mobile.viewport", ("url", url))
            : !viewport.Contains("width=device-width", StringComparison.OrdinalIgnoreCase) ? Finding.Warn("mobile.viewport", ("url", url), ("value", viewport))
            : Finding.Pass("mobile.viewport"));
        Add(viewport is null ? Finding.NotApplicable("mobile.zoom-disabled")
            : ZoomBlock(viewport) is { } zoom ? Finding.Fail("mobile.zoom-disabled", ("url", url), ("value", zoom)) : Finding.Pass("mobile.zoom-disabled"));
        Add(Charset(page, html, url));
        Add(html.Meta("theme-color") is null ? Finding.Warn("mobile.theme-color", ("url", url)) : Finding.Pass("mobile.theme-color"));
        Add(html.Document.QuerySelectorAll("meta[name]").Any(m => m.GetAttribute("name")!.StartsWith("apple-mobile-web-app", StringComparison.OrdinalIgnoreCase))
            ? Finding.Warn("mobile.apple-web-app", ("url", url)) : Finding.Pass("mobile.apple-web-app"));
        var amp = html.Document.DocumentElement.HasAttribute("amp") || html.Document.DocumentElement.HasAttribute("⚡");
        var ampLink = html.Links("amphtml").FirstOrDefault()?.GetAttribute("href");
        Add(amp ? Finding.Warn("mobile.amp", ("url", url), ("value", ""))
            : ampLink is not null ? Finding.Warn("mobile.amp", ("url", url), ("value", " (" + ampLink + ")"))
            : Finding.Pass("mobile.amp"));

        // ---- Technical -------------------------------------------------------------------------------------------
        Add(html.ParseErrors > 0 ? Finding.Warn("technical.html-validity", ("url", url), ("n", html.ParseErrors)) : Finding.Pass("technical.html-validity"));

        return findings;
    }

    // ---- Individual checks ----------------------------------------------------------------------------------------

    private static CheckFinding Canonical(PageInput page, HtmlPage html, string url)
    {
        var canonicals = html.Links("canonical").Select(l => l.GetAttribute("href")).Where(h => !string.IsNullOrWhiteSpace(h)).ToList();
        if (canonicals.Count == 0)
            return Finding.Warn("seo.canonical", ("url", url), ("target", "(none)"), ("status", "missing"));
        if (canonicals.Distinct(StringComparer.Ordinal).Count() > 1)
            return Finding.Fail("seo.canonical", ("url", url), ("target", string.Join(" | ", canonicals)), ("status", "several"));
        if (!IsHttpUrl(canonicals[0]!))
            return Finding.Warn("seo.canonical", ("url", url), ("target", canonicals[0]), ("status", "relative"));
        return Finding.Pass("seo.canonical", ("target", canonicals[0]));
    }

    /// <summary>The page's canonical URL, absolute, when it declares exactly one.</summary>
    public static Uri? CanonicalOf(PageInput page)
    {
        var hrefs = page.Html?.Links("canonical").Select(l => l.GetAttribute("href")).Where(h => !string.IsNullOrWhiteSpace(h)).Distinct().ToList();
        return hrefs is { Count: 1 } ? CrawlUrl.Resolve(page.Url, hrefs[0]) : null;
    }

    /// <summary>The page's hreflang alternates (language → absolute URL), at most 50.</summary>
    public static IReadOnlyList<(string Lang, Uri Url)> AlternatesOf(PageInput page)
        => page.Html?.Links("alternate")
            .Where(l => l.HasAttribute("hreflang"))
            .Select(l => (Lang: l.GetAttribute("hreflang")!.Trim(), Url: CrawlUrl.Resolve(page.Url, l.GetAttribute("href"))))
            .Where(a => a.Url is not null)
            .Select(a => (a.Lang, a.Url!))
            .Take(50)
            .ToList() ?? [];

    private static CheckFinding Hreflang(PageInput page, HtmlPage html, string url)
    {
        var alternates = AlternatesOf(page);
        if (alternates.Count == 0)
            return Finding.NotApplicable("seo.hreflang");
        var invalid = alternates.FirstOrDefault(a => !a.Lang.Equals("x-default", StringComparison.OrdinalIgnoreCase) && !IsLanguageTag(a.Lang));
        if (invalid.Url is not null)
            return Finding.Fail("seo.hreflang", ("url", url), ("target", invalid.Url.AbsoluteUri), ("value", "invalid code " + invalid.Lang));
        if (!alternates.Any(a => a.Url.AbsoluteUri == page.Url.AbsoluteUri))
            return Finding.Fail("seo.hreflang", ("url", url), ("target", url), ("value", "no self-reference"));
        return Finding.Pass("seo.hreflang");
    }

    /// <summary>A BCP 47 tag of the forms search engines use: a language, optionally a script and a region.</summary>
    public static bool IsLanguageTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag) || tag.Length > 35)
            return false;
        return LanguageTag().IsMatch(tag.Trim());
    }

    [GeneratedRegex(@"^[A-Za-z]{2,3}(-[A-Za-z]{4})?(-([A-Za-z]{2}|[0-9]{3}))?(-[A-Za-z0-9]{5,8})*$", RegexOptions.None, matchTimeoutMilliseconds: 500)]
    private static partial Regex LanguageTag();

    private sealed record JsonLdBlock(bool Valid, string? Problem, IReadOnlyList<string> Types, bool HasSearchAction);

    private static readonly Dictionary<string, string[][]> RequiredProperties = new(StringComparer.Ordinal)
    {
        // Each inner array is "one of" (any of them satisfies the requirement).
        ["Product"] = [["name"], ["offers", "review", "aggregateRating"]],
        ["Article"] = [["headline"]],
        ["NewsArticle"] = [["headline"]],
        ["BlogPosting"] = [["headline"]],
        ["BreadcrumbList"] = [["itemListElement"]],
        ["Organization"] = [["name", "url"]],
        ["LocalBusiness"] = [["name"], ["address"]],
        ["Event"] = [["name"], ["startDate"], ["location"]],
        ["Recipe"] = [["name"], ["image"]],
        ["FAQPage"] = [["mainEntity"]],
        ["JobPosting"] = [["title"], ["datePosted"], ["description"], ["hiringOrganization"]],
        ["VideoObject"] = [["name"], ["thumbnailUrl"], ["uploadDate"]],
    };

    /// <summary>Types Google stopped showing as rich results (research §1.1: June 2025 and January 2026 removals, FAQ/HowTo restricted).</summary>
    public static readonly IReadOnlySet<string> DeprecatedRichResultTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "ClaimReview", "SpecialAnnouncement", "Course", "CourseInstance", "EstimatedSalary", "Occupation", "LearningResource", "VehicleListing", "HowTo",
    };

    private static List<JsonLdBlock> JsonLd(HtmlPage html)
    {
        var blocks = new List<JsonLdBlock>();
        foreach (var script in html.Document.QuerySelectorAll("script[type]")
                     .Where(s => string.Equals(s.GetAttribute("type")?.Trim(), "application/ld+json", StringComparison.OrdinalIgnoreCase)).Take(20))
        {
            var text = script.TextContent;
            if (text.Length > 256 * 1024)
            {
                blocks.Add(new JsonLdBlock(false, "block too large", [], false));
                continue;
            }
            try
            {
                using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Skip });
                var types = new List<string>();
                var search = false;
                string? problem = null;
                foreach (var node in Nodes(document.RootElement))
                {
                    var nodeTypes = TypesOf(node);
                    types.AddRange(nodeTypes);
                    if (node.TryGetProperty("potentialAction", out var action) && action.ToString().Contains("SearchAction", StringComparison.Ordinal))
                        search = true;
                    foreach (var type in nodeTypes)
                        if (problem is null && RequiredProperties.TryGetValue(type, out var required))
                            foreach (var oneOf in required)
                                if (!oneOf.Any(p => node.TryGetProperty(p, out _)))
                                {
                                    problem = $"{type} is missing {string.Join(" or ", oneOf)}";
                                    break;
                                }
                }
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object && !root.TryGetProperty("@context", out _) && !root.TryGetProperty("@graph", out _))
                    problem ??= "no @context";
                if (types.Count == 0)
                    problem ??= "no @type";
                blocks.Add(new JsonLdBlock(problem is null, problem, types, search));
            }
            catch (JsonException ex)
            {
                blocks.Add(new JsonLdBlock(false, $"invalid JSON at line {ex.LineNumber + 1}", [], false));
            }
        }
        return blocks;
    }

    /// <summary>The objects of a JSON-LD document that carry an <c>@type</c>: the root, arrays and <c>@graph</c>, one level of nesting.</summary>
    private static IEnumerable<JsonElement> Nodes(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray().Take(100))
                foreach (var node in Nodes(item))
                    yield return node;
            yield break;
        }
        if (root.ValueKind != JsonValueKind.Object)
            yield break;
        if (root.TryGetProperty("@graph", out var graph) && graph.ValueKind == JsonValueKind.Array)
            foreach (var item in graph.EnumerateArray().Take(100))
                if (item.ValueKind == JsonValueKind.Object)
                    yield return item;
        if (root.TryGetProperty("@type", out _))
            yield return root;
    }

    private static IEnumerable<string> TypesOf(JsonElement node)
    {
        if (!node.TryGetProperty("@type", out var type))
            return [];
        return type.ValueKind switch
        {
            JsonValueKind.String => [Short(type.GetString()!)],
            JsonValueKind.Array => type.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => Short(t.GetString()!)).ToList(),
            _ => [],
        };

        static string Short(string t) => t.Contains('/') ? t[(t.LastIndexOf('/') + 1)..] : t;
    }

    private static CheckFinding StructuredData(List<JsonLdBlock> blocks, string url)
    {
        if (blocks.Count == 0)
            return Finding.NotApplicable("seo.structured-data");
        var bad = blocks.FirstOrDefault(b => !b.Valid);
        if (bad is null)
            return Finding.Pass("seo.structured-data", ("value", string.Join(", ", blocks.SelectMany(b => b.Types).Distinct().Take(10))));
        return bad.Problem!.StartsWith("invalid JSON", StringComparison.Ordinal) || bad.Problem == "block too large"
            ? Finding.Fail("seo.structured-data", ("url", url), ("value", bad.Problem))
            : Finding.Warn("seo.structured-data", ("url", url), ("value", bad.Problem));
    }

    private static readonly IReadOnlySet<string> GenericLinkTexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "click here", "here", "read more", "more", "learn more", "this", "link", "click", "go", "continue", "details", "more info",
        "klik hier", "hier", "lees meer", "meer", "meer info", "meer lezen", "deze link", "verder",
    };

    private static CheckFinding LinkText(HtmlPage html, string url)
    {
        var links = html.Document.QuerySelectorAll("a[href]");
        if (links.Length == 0)
            return Finding.NotApplicable("seo.link-text");
        var bad = links.Count(a =>
        {
            var name = HtmlPage.Collapse(a.TextContent);
            if (name.Length == 0)
                name = a.GetAttribute("aria-label") ?? a.QuerySelector("img[alt]")?.GetAttribute("alt") ?? a.GetAttribute("title") ?? string.Empty;
            if (name.Length == 0 && a.HasAttribute("aria-labelledby"))
                return false; // named by another element: assume it is
            return name.Trim().Length == 0 || GenericLinkTexts.Contains(name.Trim().TrimEnd('.', '…', '»', '›', '>').Trim());
        });
        return bad > 0 ? Finding.Warn("seo.link-text", ("url", url), ("n", bad)) : Finding.Pass("seo.link-text");
    }

    /// <summary>Why a URL is not clean, or null.</summary>
    public static string? UrlProblem(Uri uri)
    {
        var path = uri.AbsolutePath;
        if (uri.AbsoluteUri.Length > 115)
            return "long";
        if (path.Any(char.IsUpper))
            return "uppercase";
        if (path.Contains('_'))
            return "underscore";
        if (SessionId().IsMatch(uri.Query))
            return "session id";
        if (uri.OriginalString.Any(c => c > 127))
            return "unencoded characters";
        return null;
    }

    [GeneratedRegex(@"(^|[?&;])(sid|sessionid|session_id|jsessionid|phpsessid|aspsessionid[a-z]*)=", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 500)]
    private static partial Regex SessionId();

    private static readonly (string Host, string Label)[] ShareEndpoints =
    [
        ("facebook.com/sharer", "Facebook"), ("twitter.com/intent", "X"), ("x.com/intent", "X"), ("linkedin.com/sharearticle", "LinkedIn"),
        ("linkedin.com/shareArticle", "LinkedIn"), ("linkedin.com/sharing", "LinkedIn"), ("wa.me/", "WhatsApp"), ("api.whatsapp.com/send", "WhatsApp"),
        ("pinterest.com/pin/create", "Pinterest"), ("reddit.com/submit", "Reddit"), ("t.me/share", "Telegram"),
    ];

    private static List<string> ShareTargets(HtmlPage html)
    {
        var hrefs = html.Document.QuerySelectorAll("a[href]").Select(a => a.GetAttribute("href") ?? string.Empty).ToList();
        return ShareEndpoints.Where(e => hrefs.Any(h => h.Contains(e.Host, StringComparison.OrdinalIgnoreCase))).Select(e => e.Label).Distinct().ToList();
    }

    public static bool IsCompressed(string? encoding)
        => encoding is { } e && (e.Contains("br", StringComparison.OrdinalIgnoreCase) || e.Contains("gzip", StringComparison.OrdinalIgnoreCase)
            || e.Contains("zstd", StringComparison.OrdinalIgnoreCase) || e.Contains("deflate", StringComparison.OrdinalIgnoreCase));

    private static CheckFinding Hsts(PageInput page)
    {
        if (!page.IsHttps)
            return Finding.NotApplicable("security.hsts");
        var header = page.Header("strict-transport-security");
        if (header is null)
            return Finding.Fail("security.hsts");
        var maxAge = MaxAge().Match(header);
        if (!maxAge.Success || !long.TryParse(maxAge.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds < 31_536_000)
            return Finding.Warn("security.hsts", ("value", header));
        return Finding.Pass("security.hsts", ("value", header));
    }

    [GeneratedRegex(@"max-age\s*=\s*""?(\d+)", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 500)]
    private static partial Regex MaxAge();

    private static CheckFinding MixedContent(PageInput page, HtmlPage html, string url)
    {
        if (!page.IsHttps)
            return Finding.NotApplicable("security.mixed-content");
        var active = new List<string>();
        var passive = new List<string>();
        foreach (var element in html.Document.QuerySelectorAll("script[src], iframe[src], object[data], link[href], img[src], video[src], audio[src], source[src], embed[src]"))
        {
            var attribute = element.LocalName switch { "object" => "data", "link" => "href", _ => "src" };
            var value = element.GetAttribute(attribute)?.Trim();
            if (value is null || !value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                continue;
            if (element.LocalName == "link" && !HtmlPage.HasToken(element.GetAttribute("rel"), "stylesheet") && !HtmlPage.HasToken(element.GetAttribute("rel"), "icon")
                && !HtmlPage.HasToken(element.GetAttribute("rel"), "manifest"))
                continue;
            (element.LocalName is "script" or "iframe" or "object" or "link" or "embed" ? active : passive).Add(value);
        }
        if (active.Count + passive.Count == 0)
            return Finding.Pass("security.mixed-content");
        var sample = active.Concat(passive).First();
        return Finding.Make("security.mixed-content", active.Count > 0 ? CheckStatus.Fail : CheckStatus.Warn,
            [("url", url), ("n", active.Count + passive.Count), ("target", sample)]);
    }

    /// <summary>What a policy allows that it should not (CSP Evaluator's main rules), or null.</summary>
    public static string? CspWeakness(string policy)
    {
        var directives = policy.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length > 0)
            .GroupBy(p => p[0].ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First().Skip(1).Select(s => s.ToLowerInvariant()).ToArray());
        var scripts = directives.TryGetValue("script-src", out var s) ? s : directives.TryGetValue("default-src", out var d) ? d : null;
        if (scripts is null)
            return "scripts from anywhere (no script-src or default-src)";
        var problems = new List<string>();
        var strict = scripts.Any(v => v.StartsWith("'nonce-", StringComparison.Ordinal) || v.StartsWith("'sha", StringComparison.Ordinal) || v == "'strict-dynamic'");
        if (scripts.Contains("'unsafe-inline'") && !strict)
            problems.Add("'unsafe-inline' scripts");
        if (scripts.Contains("'unsafe-eval'"))
            problems.Add("'unsafe-eval'");
        if (scripts.Any(v => v is "*" or "http:" or "https:" or "data:"))
            problems.Add("scripts from any host");
        if (!directives.ContainsKey("object-src") && !(directives.TryGetValue("default-src", out var def) && def.Contains("'none'")))
            problems.Add("plugins (no object-src 'none')");
        return problems.Count == 0 ? null : string.Join(", ", problems);
    }

    private static string? ServerSignature(PageInput page)
    {
        var found = new List<string>();
        if (page.Header("server") is { } server && server.Any(char.IsDigit))
            found.Add("Server: " + server);
        foreach (var header in new[] { "x-powered-by", "x-aspnet-version", "x-aspnetmvc-version" })
            if (page.Header(header) is { } value)
                found.Add($"{header}: {value}");
        return found.Count == 0 ? null : string.Join("; ", found);
    }

    /// <summary>Third-party script hosts whose files change by design, so they cannot carry an integrity hash.</summary>
    private static readonly string[] DynamicScriptHosts =
    [
        "googletagmanager.com", "google-analytics.com", "www.google.com", "gstatic.com", "connect.facebook.net", "js.stripe.com",
        "maps.googleapis.com", "platform.twitter.com", "static.cloudflareinsights.com", "challenges.cloudflare.com", "plausible.io",
        "cdn.cookielaw.org", "consent.cookiebot.com", "snap.licdn.com", "js.hs-scripts.com", "fonts.googleapis.com",
    ];

    private static CheckFinding Sri(PageInput page, HtmlPage html, string url)
    {
        var crossOrigin = html.Document.QuerySelectorAll("script[src], link[href]")
            .Where(e => e.LocalName == "script" || HtmlPage.HasToken(e.GetAttribute("rel"), "stylesheet"))
            .Select(e => (Element: e, Url: CrawlUrl.Resolve(page.Url, e.GetAttribute(e.LocalName == "script" ? "src" : "href"))))
            .Where(x => x.Url is not null && !string.Equals(x.Url.Host, page.Url.Host, StringComparison.OrdinalIgnoreCase)
                && !DynamicScriptHosts.Any(h => x.Url.Host.EndsWith(h, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (crossOrigin.Count == 0)
            return Finding.NotApplicable("security.sri");
        var missing = crossOrigin.Count(x => !x.Element.HasAttribute("integrity"));
        return missing > 0 ? Finding.Warn("security.sri", ("url", url), ("n", missing)) : Finding.Pass("security.sri");
    }

    private static CheckFinding Csrf(HtmlPage html, string url)
    {
        var posts = html.Document.QuerySelectorAll("form").Where(f => string.Equals(f.GetAttribute("method")?.Trim(), "post", StringComparison.OrdinalIgnoreCase)).ToList();
        if (posts.Count == 0)
            return Finding.NotApplicable("security.csrf-heuristic");
        var without = posts.Count(f => !f.QuerySelectorAll("input[type=hidden][name]").Any(i => TokenField().IsMatch(i.GetAttribute("name")!)));
        return without > 0 ? Finding.Warn("security.csrf-heuristic", ("url", url), ("n", without)) : Finding.Pass("security.csrf-heuristic");
    }

    [GeneratedRegex(@"csrf|xsrf|token|authenticity|nonce|requestverification", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 500)]
    private static partial Regex TokenField();

    private static CheckFinding CookieFlags(PageInput page)
    {
        if (page.SetCookies.Count == 0)
            return Finding.NotApplicable("security.cookie-flags");
        foreach (var cookie in page.SetCookies.Take(50))
        {
            var parts = cookie.Split(';', StringSplitOptions.TrimEntries);
            var name = parts[0].Split('=', 2)[0];
            bool Has(string flag) => parts.Skip(1).Any(p => p.StartsWith(flag, StringComparison.OrdinalIgnoreCase));
            var sessionLike = SessionCookie().IsMatch(name);
            if ((page.IsHttps && !Has("secure")) || !Has("samesite") || (sessionLike && !Has("httponly")))
                return Finding.Warn("security.cookie-flags", ("value", name));
        }
        return Finding.Pass("security.cookie-flags");
    }

    [GeneratedRegex(@"sess|auth|token|login|sid|identity|jwt", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 500)]
    private static partial Regex SessionCookie();

    private static CheckFinding FormLabels(HtmlPage html, string url)
    {
        var fields = html.Document.QuerySelectorAll("input, select, textarea")
            .Where(e => e.LocalName != "input" || (e.GetAttribute("type")?.Trim().ToLowerInvariant() ?? "text") is not ("hidden" or "submit" or "button" or "image" or "reset"))
            .ToList();
        if (fields.Count == 0)
            return Finding.NotApplicable("accessibility.form-labels");
        var labelled = new HashSet<string>(html.Document.QuerySelectorAll("label[for]").Select(l => l.GetAttribute("for")!), StringComparer.Ordinal);
        var unlabelled = fields.Count(f =>
            !(f.Id is { Length: > 0 } id && labelled.Contains(id))
            && f.Closest("label") is null
            && string.IsNullOrWhiteSpace(f.GetAttribute("aria-label"))
            && string.IsNullOrWhiteSpace(f.GetAttribute("aria-labelledby"))
            && string.IsNullOrWhiteSpace(f.GetAttribute("title")));
        return unlabelled > 0 ? Finding.Fail("accessibility.form-labels", ("url", url), ("n", unlabelled)) : Finding.Pass("accessibility.form-labels");
    }

    /// <summary>What in a viewport blocks zooming, or null.</summary>
    public static string? ZoomBlock(string viewport)
    {
        var settings = viewport.Split(',', ';').Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
            .GroupBy(p => p[0].Trim().ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First()[1].Trim().ToLowerInvariant());
        if (settings.TryGetValue("user-scalable", out var scalable) && scalable is "no" or "0")
            return "user-scalable=" + scalable;
        if (settings.TryGetValue("maximum-scale", out var max) && double.TryParse(max, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale) && scale < 2)
            return "maximum-scale=" + max;
        return null;
    }

    private static CheckFinding Charset(PageInput page, HtmlPage html, string url)
    {
        var header = page.ContentType is { } type && CharsetIn().Match(type) is { Success: true } m ? m.Groups[1].Value : null;
        var early = page.Prefix is { } prefix && CharsetMeta().Match(prefix) is { Success: true } meta ? meta.Groups[1].Value : null;
        var declared = header ?? early;
        if (declared is null)
            return Finding.Fail("mobile.charset", ("url", url));
        return declared.Trim('"', '\'').Equals("utf-8", StringComparison.OrdinalIgnoreCase) || declared.Trim('"', '\'').Equals("utf8", StringComparison.OrdinalIgnoreCase)
            ? Finding.Pass("mobile.charset") : Finding.Warn("mobile.charset", ("url", url), ("value", declared));
    }

    [GeneratedRegex(@"charset\s*=\s*""?([\w-]+)", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 500)]
    private static partial Regex CharsetIn();

    [GeneratedRegex(@"<meta[^>]+charset\s*=\s*[""']?([\w-]+)", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 500)]
    private static partial Regex CharsetMeta();

    /// <summary>
    /// An absolute http(s) URL. The scheme check is what makes "/x" relative on every OS: on Linux,
    /// Uri.TryCreate(UriKind.Absolute) accepts "/x" as file:///x.
    /// </summary>
    private static bool IsHttpUrl(string value)
        => Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
