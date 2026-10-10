using System.Net;
using System.Text;

namespace SeoTester.Pages;

/// <summary>
/// What SeoTester's server-rendered pages share (<c>/bot</c>, <c>/privacy</c>, <c>/terms</c>): the language choice, the
/// document head and its small stylesheet, and HTML encoding. No script: the readers include site operators following a
/// URL from their logs with curl or a text browser.
/// </summary>
internal static class ServerPage
{
    /// <summary><c>?lang=nl|en</c> wins, then the browser's preferred of English and Dutch; English otherwise.</summary>
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

    public static string E(string text) => WebUtility.HtmlEncode(text);

    /// <summary>Opens the document: doctype, language, title, the shared stylesheet, <c>&lt;main&gt;</c>.</summary>
    public static StringBuilder Start(bool nl, string title)
        => new StringBuilder()
            .Append("<!doctype html>\n<html lang=\"").Append(nl ? "nl" : "en").Append("\">\n<head>\n<meta charset=\"utf-8\">\n")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n<meta name=\"color-scheme\" content=\"light dark\">\n")
            .Append("<title>").Append(E(title)).Append("</title>\n")
            .Append("<style>body{font-family:system-ui,sans-serif;max-width:46rem;margin:2rem auto;padding:0 1rem;line-height:1.55}")
            .Append("code,pre{font-family:ui-monospace,monospace}pre{padding:.75rem;border:1px solid #8884;border-radius:.375rem;overflow-x:auto}")
            .Append("h1{font-size:1.6rem}h2{font-size:1.15rem;margin-top:1.75rem}</style>\n</head>\n<body>\n<main>\n");

    /// <summary>Closes what <see cref="Start"/> opened.</summary>
    public static string End(StringBuilder sb) => sb.Append("</main>\n</body>\n</html>\n").ToString();
}
