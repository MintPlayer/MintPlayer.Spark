using System.Diagnostics.CodeAnalysis;

namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// The one rule for a link Spark stores and a browser later follows, such as a program unit's <c>url</c> or a
/// notification's <c>Url</c>: an app path (<c>/runs/42</c>) or an absolute <c>http</c>/<c>https</c> URL with a host.
/// Everything else is refused: a blank value, whitespace or a control character anywhere, a <c>\</c>, credentials
/// (<c>https://user:pass@host</c>), and every other scheme (<c>javascript:</c>, <c>data:</c>, <c>file:</c>,
/// <c>mailto:</c>, …).
/// </summary>
/// <remarks>
/// <para>
/// Decided on the text, never by <see cref="Uri.TryCreate(string, UriKind, out Uri)"/> alone: on Linux that accepts
/// <c>/runs/42</c> as <c>file:///runs/42</c>, which a test on Windows cannot catch, so the scheme is tested explicitly.
/// </para>
/// <para>
/// Stricter than a browser, because a browser cleans a URL before it reads it: tabs and newlines are removed anywhere
/// and <c>\</c> reads as <c>/</c>. <c>/&lt;TAB&gt;/evil.example</c> and <c>/\evil.example</c> look like app paths here
/// and are the protocol-relative <c>//evil.example</c>, another host, to the browser.
/// </para>
/// </remarks>
public static class SparkLinkUrl
{
    /// <summary>The two allowed forms, for messages.</summary>
    public const string AllowedForms = "an app path ('/runs/42') or an absolute http(s) URL ('https://example.com/docs')";

    /// <summary>Whether <paramref name="url"/> is an app path or an absolute <c>http</c>/<c>https</c> URL with a host.</summary>
    public static bool IsAllowed([NotNullWhen(true)] string? url)
        => IsClean(url) && (url[0] == '/' ? IsAppPathText(url) : IsAbsoluteHttp(url));

    /// <summary>
    /// Whether <paramref name="url"/> is an allowed app path: one leading <c>/</c>, not the <c>//</c> a browser reads
    /// as another host. It resolves below the application's base, like a router link.
    /// </summary>
    public static bool IsAppPath([NotNullWhen(true)] string? url)
        => IsClean(url) && IsAppPathText(url);

    private static bool IsClean([NotNullWhen(true)] string? url)
    {
        if (string.IsNullOrEmpty(url))
            return false;
        foreach (var c in url)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c) || c == '\\')
                return false;
        }
        return true;
    }

    private static bool IsAppPathText(string url) => url[0] == '/' && (url.Length == 1 || url[1] != '/');

    private static bool IsAbsoluteHttp(string url)
    {
        var authorityStart = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "https://".Length
            : url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "http://".Length
            : 0;
        if (authorityStart == 0)
            return false;

        // The authority on the text: none ("https:///x", which a browser reads as host x) or one with credentials
        // ("https://github.com@evil.example/", which reads as github.com and goes to evil.example) is refused.
        var authorityEnd = url.IndexOfAny(['/', '?', '#'], authorityStart);
        var authority = url.AsSpan(authorityStart, (authorityEnd < 0 ? url.Length : authorityEnd) - authorityStart);
        if (authority.IsEmpty || authority.Contains('@'))
            return false;

        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            && uri.Host.Length > 0;
    }
}
