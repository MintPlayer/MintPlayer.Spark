using System.Globalization;
using System.Net;

namespace SeoTester.Sites;

/// <summary>
/// What a user types as a site, reduced to the origin SeoTester audits (<c>docs/seo_analyzer_PRD.md</c> F2):
/// <c>http</c> or <c>https</c>, the host in lower-case ASCII (IDN → punycode, no trailing dot), and the port only when it
/// is not the scheme's default. Path, query, fragment are dropped; user info, IP literals and single-label hosts are
/// refused (an audit is of a public website; the SSRF-safe client refuses private addresses on top of this).
/// </summary>
public static class SiteOrigin
{
    /// <summary>The normalized origin and host, or the reason it is not a website.</summary>
    public static bool TryNormalize(string? input, out string origin, out string host, out string? problem)
    {
        origin = host = string.Empty;
        problem = null;
        var text = input?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            problem = "Enter the address of the website, e.g. https://example.com.";
            return false;
        }
        if (text.Length > 2048)
        {
            problem = "That address is too long.";
            return false;
        }
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            problem = "Only http:// and https:// websites can be audited.";
            return false;
        }
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            problem = "Leave the user name and password out of the address.";
            return false;
        }
        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 || IPAddress.TryParse(uri.Host.Trim('[', ']'), out _))
        {
            problem = "Enter the website's domain name, not an IP address.";
            return false;
        }

        string ascii;
        try
        {
            ascii = new IdnMapping().GetAscii(uri.Host.TrimEnd('.')).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            problem = "That is not a valid domain name.";
            return false;
        }
        if (ascii.Length > 253 || !ascii.Contains('.') || ascii.Split('.').Any(label => label.Length is 0 or > 63 || !label.All(IsHostChar) || label.StartsWith('-') || label.EndsWith('-')))
        {
            problem = "That is not a public domain name.";
            return false;
        }

        host = ascii;
        origin = uri.IsDefaultPort ? $"{uri.Scheme}://{ascii}" : $"{uri.Scheme}://{ascii}:{uri.Port.ToString(CultureInfo.InvariantCulture)}";
        return true;
    }

    private static bool IsHostChar(char c) => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-';
}
