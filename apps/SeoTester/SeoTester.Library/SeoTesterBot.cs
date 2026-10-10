namespace SeoTester;

/// <summary>
/// Who the crawler says it is (<c>docs/seo_analyzer_PRD.md</c> §6, D19, D27, D29). The product token is also the
/// only opt-out: a site that writes <c>User-agent: MintPlayerSeoTester</c> / <c>Disallow: /</c> in its robots.txt is
/// never crawled. The URL in the user agent is the <c>/bot</c> page, which explains all of this to site owners.
/// </summary>
public static class SeoTesterBot
{
    /// <summary>The robots.txt token (RFC 9309 product token) the crawler obeys.</summary>
    public const string RobotsToken = "MintPlayerSeoTester";

    /// <summary>The path of the page the user agent points at.</summary>
    public const string PagePath = "/bot";

    /// <summary>The production page the user agent points at.</summary>
    public const string PageUrl = "https://seotester.mintplayer.com" + PagePath;

    /// <summary>The <c>User-Agent</c> header every request of the crawler sends.</summary>
    public const string UserAgent = RobotsToken + "/1.0 (+" + PageUrl + ")";
}
