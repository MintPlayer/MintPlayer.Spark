using System.Security.Cryptography;
using System.Text;

namespace SeoTester.Crawling;

/// <summary>
/// URLs as the crawler keeps them (<c>docs/seo_analyzer_PRD.md</c> §4.3): absolute http(s), no fragment, no user
/// info, the scheme and host in lower case and the default port dropped (<see cref="Uri.AbsoluteUri"/> already does
/// the last two). Everything here reads untrusted markup, so it never throws on bad input: a bad link is skipped.
/// </summary>
public static class CrawlUrl
{
    /// <summary>Longest URL the crawler follows; longer ones are skipped (and counted by the URL-hygiene check).</summary>
    public const int MaxLength = 2048;

    /// <summary><paramref name="href"/> resolved against <paramref name="baseUri"/>, or null when it is no crawlable http(s) URL.</summary>
    public static Uri? Resolve(Uri baseUri, string? href)
    {
        if (string.IsNullOrWhiteSpace(href) || href.Length > MaxLength)
            return null;
        var text = href.Trim();
        if (text.StartsWith('#') || text.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) || text.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!Uri.TryCreate(baseUri, text, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || !string.IsNullOrEmpty(uri.UserInfo))
            return null;
        return Normalize(uri);
    }

    /// <summary><paramref name="uri"/> without its fragment.</summary>
    public static Uri Normalize(Uri uri)
    {
        if (string.IsNullOrEmpty(uri.Fragment))
            return uri;
        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        return builder.Uri;
    }

    /// <summary>The origin of <paramref name="uri"/> in <c>SiteOrigin</c>'s form: scheme, host and a non-default port.</summary>
    public static string OriginOf(Uri uri)
        => uri.IsDefaultPort ? $"{uri.Scheme}://{uri.IdnHost.ToLowerInvariant()}" : $"{uri.Scheme}://{uri.IdnHost.ToLowerInvariant()}:{uri.Port}";

    /// <summary>Whether <paramref name="uri"/> is on <paramref name="origin"/> (scheme, host and port).</summary>
    public static bool IsOn(Uri uri, string origin) => string.Equals(OriginOf(uri), origin, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="uri"/> is on the host of the site, whatever the scheme (an http link to an https site is internal, but mixed).</summary>
    public static bool IsSameHost(Uri uri, string host) => string.Equals(uri.IdnHost, host, StringComparison.OrdinalIgnoreCase);

    /// <summary>A short, stable key for a URL: the first 16 hex digits of its SHA-256 (the frontier's visited set).</summary>
    public static string Hash(string url) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16];
}
