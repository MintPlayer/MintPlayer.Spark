using System.Globalization;
using System.Text;
using System.Net;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Settings;
using SeoTester.Settings;

namespace SeoTester.Bot;

/// <summary>
/// <c>GET /bot</c>: the page the crawler's user agent points at (<c>docs/seo_analyzer_PRD.md</c> F19, D17, D19):
/// who crawls, why, how politely, and the robots.txt opt-out. Rendered by the server, not the SPA: the readers are
/// site operators following a URL from their access logs (often with curl or a text browser), so it must read
/// without JavaScript, and its limits are the current administrator settings, not numbers copied into a page.
/// Anonymous; English, or Dutch for a browser that prefers it (<c>?lang=nl</c> forces it).
/// </summary>
internal sealed partial class BotPageEndpoint : IGetEndpoint
{
    public static string Path => SeoTesterBot.PagePath;

    [Inject] private readonly ISparkSettings settings;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var ct = httpContext.RequestAborted;
        var limits = new BotPageLimits(
            await settings.GetAsync(SeoSettings.UnverifiedPagesPerRun, ct),
            await settings.GetAsync(SeoSettings.UnverifiedConnectionsPerHost, ct),
            await settings.GetAsync(SeoSettings.UnverifiedRequestInterval, ct),
            await settings.GetAsync(SeoSettings.UnverifiedWallTime, ct),
            await settings.GetAsync(SeoSettings.TargetCoolDown, ct),
            await settings.GetAsync(SeoSettings.IntrusiveProbes, ct));

        var html = BotPage.Render(BotPage.LanguageFor(httpContext.Request), limits);
        httpContext.Response.Headers.CacheControl = "public, max-age=300";
        return Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8);
    }
}

/// <summary>The current limits the page states.</summary>
internal sealed record BotPageLimits(
    int PagesPerRun,
    int ConnectionsPerHost,
    TimeSpan RequestInterval,
    TimeSpan WallTime,
    TimeSpan TargetCoolDown,
    SeoFeatureScope IntrusiveProbes);

/// <summary>The <c>/bot</c> page's HTML, in English or Dutch. Every value is encoded.</summary>
internal static class BotPage
{
    public static string LanguageFor(HttpRequest request)
    {
        if (request.Query["lang"].ToString() is { Length: > 0 } forced)
            return forced.StartsWith("nl", StringComparison.OrdinalIgnoreCase) ? "nl" : "en";

        var preferred = request.GetTypedHeaders().AcceptLanguage
            .OrderByDescending(l => l.Quality ?? 1)
            .Select(l => l.Value.ToString())
            .FirstOrDefault(l => l.StartsWith("nl", StringComparison.OrdinalIgnoreCase) || l.StartsWith("en", StringComparison.OrdinalIgnoreCase));
        return preferred?.StartsWith("nl", StringComparison.OrdinalIgnoreCase) == true ? "nl" : "en";
    }

    public static string Render(string language, BotPageLimits limits)
    {
        var nl = language == "nl";

        static string E(string text) => WebUtility.HtmlEncode(text);

        var interval = Duration(limits.RequestInterval, nl);
        var wallTime = Duration(limits.WallTime, nl);
        var coolDown = Duration(limits.TargetCoolDown, nl);
        var probes = limits.IntrusiveProbes switch
        {
            SeoFeatureScope.Off => nl ? "Nooit." : "Never.",
            SeoFeatureScope.AllSites => nl
                ? "Ook op sites zonder bewezen eigenaarschap, op last van de beheerder."
                : "Also on sites without proven ownership, as the operator configured.",
            _ => nl
                ? "Alleen op sites waarvan de eigenaar het eigenaarschap bewees (DNS, meta-tag of bestand)."
                : "Only on sites whose owner proved ownership (DNS, meta tag or file).",
        };

        var sb = new StringBuilder();
        sb.Append("<!doctype html>\n<html lang=\"").Append(nl ? "nl" : "en").Append("\">\n<head>\n<meta charset=\"utf-8\">\n")
          .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n<meta name=\"color-scheme\" content=\"light dark\">\n")
          .Append("<title>").Append(E(nl ? "MintPlayerSeoTester, de crawler van SeoTester" : "MintPlayerSeoTester, the SeoTester crawler")).Append("</title>\n")
          .Append("<style>body{font-family:system-ui,sans-serif;max-width:46rem;margin:2rem auto;padding:0 1rem;line-height:1.55}")
          .Append("code,pre{font-family:ui-monospace,monospace}pre{padding:.75rem;border:1px solid #8884;border-radius:.375rem;overflow-x:auto}")
          .Append("h1{font-size:1.6rem}h2{font-size:1.15rem;margin-top:1.75rem}</style>\n</head>\n<body>\n<main>\n");

        sb.Append("<h1>").Append(E(nl ? "MintPlayerSeoTester, de crawler van SeoTester" : "MintPlayerSeoTester, the SeoTester crawler")).Append("</h1>\n");
        sb.Append("<p>").Append(E(nl
            ? "Je vond deze pagina waarschijnlijk in je toegangslogboek. Deze user agent hoort bij SeoTester, een dienst die websites nakijkt op SEO, prestaties, beveiliging, toegankelijkheid en sociale previews:"
            : "You probably found this page in your access log. This user agent belongs to SeoTester, a service that checks websites for SEO, performance, security, accessibility and social previews:"))
          .Append("</p>\n<pre><code data-user-agent>").Append(E(SeoTesterBot.UserAgent)).Append("</code></pre>\n");

        sb.Append("<h2>").Append(E(nl ? "Waarom je site bezocht wordt" : "Why your site is visited")).Append("</h2>\n<p>")
          .Append(E(nl
            ? "Een aangemelde gebruiker van SeoTester startte een controle van je site, of de eigenaar van de site plande er een. Er wordt niets geïndexeerd of gepubliceerd: de resultaten zijn enkel zichtbaar voor wie de controle startte en de leden van diens organisatie, tenzij zij een deellink maken."
            : "A signed-in SeoTester user started a check of your site, or the site's owner scheduled one. Nothing is indexed or published: the results are seen only by whoever started the check and the members of their organization, unless they make a share link."))
          .Append("</p>\n");

        sb.Append("<h2>").Append(E(nl ? "Hoe beleefd de crawler is" : "How politely it crawls")).Append("</h2>\n<ul>\n");
        Item(nl ? $"Hij volgt robots.txt (RFC 9309) voor het token {SeoTesterBot.RobotsToken}." : $"It obeys robots.txt (RFC 9309) for the token {SeoTesterBot.RobotsToken}.");
        Item(nl
            ? $"Ten hoogste {limits.ConnectionsPerHost} verbinding(en) tegelijk per host, met minstens {interval} tussen twee verzoeken."
            : $"At most {limits.ConnectionsPerHost} connection(s) at a time per host, with at least {interval} between two requests.");
        Item(nl
            ? $"Ten hoogste {limits.PagesPerRun} pagina's en {wallTime} per controle."
            : $"At most {limits.PagesPerRun} pages and {wallTime} per check.");
        Item(nl
            ? $"Eenzelfde site wordt hoogstens eens per {coolDown} gecontroleerd, wie de controle ook start."
            : $"One site is checked at most once every {coolDown}, whoever starts the check.");
        Item(nl ? "Een Retry-After bij 429 of 503 wordt gerespecteerd." : "A Retry-After on 429 or 503 is honoured.");
        Item(nl ? "Pagina's worden enkel gelezen: geen formulieren, geen aanmeldingen, geen POST-verzoeken." : "Pages are only read: no forms, no sign-ins, no POST requests.");
        Item((nl ? "Zoeken naar blootgestelde bestanden (.git, .env, back-ups): " : "Looking for exposed files (.git, .env, backups): ") + probes);
        Item(nl
            ? "Sites waarvan de eigenaar het eigenaarschap bewees, krijgen ruimere limieten: die eigenaar koos er zelf voor."
            : "Sites whose owner proved ownership get wider limits: that owner chose them.");
        sb.Append("</ul>\n");

        sb.Append("<h2>").Append(E(nl ? "De crawler weren" : "Opting out")).Append("</h2>\n<p>")
          .Append(E(nl ? "Zet dit in je robots.txt, en SeoTester controleert je site niet meer:" : "Put this in your robots.txt and SeoTester will not check your site any more:"))
          .Append("</p>\n<pre><code data-opt-out>").Append(E($"User-agent: {SeoTesterBot.RobotsToken}\nDisallow: /")).Append("</code></pre>\n<p>")
          .Append(E(nl
            ? "Dat is de enige manier om je af te melden, en ze werkt meteen: robots.txt wordt bij elke controle opnieuw gelezen. Een gedeeltelijke Disallow beperkt de controle tot de toegelaten paden."
            : "That is the only way to opt out, and it works at once: robots.txt is read again for every check. A partial Disallow limits the check to the allowed paths."))
          .Append("</p>\n");

        sb.Append("</main>\n</body>\n</html>\n");
        return sb.ToString();

        void Item(string text) => sb.Append("<li>").Append(E(text)).Append("</li>\n");
    }

    /// <summary>"1 second", "10 minutes", "6 hours" (or Dutch): the largest whole unit.</summary>
    internal static string Duration(TimeSpan value, bool nl)
    {
        static string Plural(double n, string one, string many) => n.ToString("0.##", CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many);
        if (value.TotalDays >= 1 && value.TotalDays == Math.Floor(value.TotalDays))
            return Plural(value.TotalDays, nl ? "dag" : "day", nl ? "dagen" : "days");
        if (value.TotalHours >= 1 && value.TotalHours == Math.Floor(value.TotalHours))
            return Plural(value.TotalHours, nl ? "uur" : "hour", nl ? "uur" : "hours");
        if (value.TotalMinutes >= 1 && value.TotalMinutes == Math.Floor(value.TotalMinutes))
            return Plural(value.TotalMinutes, nl ? "minuut" : "minute", nl ? "minuten" : "minutes");
        return Plural(value.TotalSeconds, nl ? "seconde" : "second", nl ? "seconden" : "seconds");
    }
}
