using System.Net;
using System.Text.RegularExpressions;

namespace SeoTester.Sites;

/// <summary>
/// The three ways an organization proves it controls a site (<c>docs/seo_analyzer_PRD.md</c> F3, D29), and how the
/// evidence is read. Everything read here comes from the site or its DNS and is untrusted: inputs are bounded, the
/// comparison is exact, and only the organization's own random token proves anything.
/// <list type="bullet">
/// <item>DNS: a TXT record at <c>_seotester.{host}</c> with the value <c>seotester-site-verification={token}</c>.</item>
/// <item>A file: <c>{origin}/.well-known/seotester-verification.txt</c> with that line.</item>
/// <item>A meta tag in the home page's <c>&lt;head&gt;</c>: <c>&lt;meta name="seotester-site-verification" content="{token}"&gt;</c>.</item>
/// </list>
/// </summary>
public static partial class OwnershipProof
{
    public const string Name = "seotester-site-verification";
    public const string WellKnownPath = "/.well-known/seotester-verification.txt";

    /// <summary>At most this much of a page or file is read.</summary>
    public const int MaxBytes = 256 * 1024;

    /// <summary>The TXT record's name for <paramref name="host"/>.</summary>
    public static string RecordName(string host) => "_seotester." + host;

    /// <summary>The TXT record value and the file's line.</summary>
    public static string ExpectedValue(string token) => Name + "=" + token;

    /// <summary>The meta tag to put in the home page's head.</summary>
    public static string MetaTag(string token) => $"<meta name=\"{Name}\" content=\"{token}\">";

    /// <summary>One of the TXT records (each the concatenation of its strings) is exactly the expected value.</summary>
    public static bool MatchesTxt(IEnumerable<string> records, string token)
        => IsToken(token) && records.Take(100).Any(r => r is { Length: <= 4096 } && string.Equals(r.Trim(), ExpectedValue(token), StringComparison.Ordinal));

    /// <summary>One line of the well-known file is exactly the expected value.</summary>
    public static bool MatchesWellKnown(string? body, string token)
        => IsToken(token) && body is not null
           && Bounded(body).Split('\n').Take(200).Any(line => string.Equals(line.Trim(), ExpectedValue(token), StringComparison.Ordinal));

    /// <summary>A meta tag named <see cref="Name"/> in the head (before <c>&lt;body</c> or <c>&lt;/head&gt;</c>, comments removed) has the token as its content.</summary>
    public static bool MatchesMeta(string? html, string token)
    {
        if (!IsToken(token) || html is null)
            return false;
        try
        {
            return MatchesMetaCore(html, token);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;   // a page built to stall the parser proves nothing
        }
    }

    private static bool MatchesMetaCore(string html, string token)
    {
        var text = CommentPattern().Replace(Bounded(html), " ");
        var end = HeadEndPattern().Match(text);
        if (end.Success)
            text = text[..end.Index];

        foreach (Match tag in MetaPattern().Matches(text))
        {
            string? name = null, content = null;
            foreach (Match attribute in AttributePattern().Matches(tag.Groups["attrs"].Value))
            {
                var value = WebUtility.HtmlDecode(attribute.Groups["dq"].Success ? attribute.Groups["dq"].Value
                    : attribute.Groups["sq"].Success ? attribute.Groups["sq"].Value : attribute.Groups["bare"].Value).Trim();
                switch (attribute.Groups["name"].Value.ToLowerInvariant())
                {
                    case "name": name ??= value; break;
                    case "content": content ??= value; break;
                }
            }
            if (string.Equals(name, Name, StringComparison.OrdinalIgnoreCase) && string.Equals(content, token, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>A token SeoTester made: 32 lower-case hex digits. Anything else (an empty token above all) proves nothing.</summary>
    public static bool IsToken(string? token) => token is { Length: 32 } && token.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>A new token.</summary>
    public static string NewToken() => Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

    private static string Bounded(string text) => text.Length > MaxBytes ? text[..MaxBytes] : text;

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CommentPattern();

    [GeneratedRegex(@"</head\s*>|<body[\s>]", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HeadEndPattern();

    [GeneratedRegex(@"<meta\b(?<attrs>[^>]{0,2000})>", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MetaPattern();

    [GeneratedRegex("""(?<name>[a-zA-Z_:][-a-zA-Z0-9_:.]*)\s*=\s*(?:"(?<dq>[^"]*)"|'(?<sq>[^']*)'|(?<bare>[^\s"'=<>`/]+))""", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AttributePattern();
}
