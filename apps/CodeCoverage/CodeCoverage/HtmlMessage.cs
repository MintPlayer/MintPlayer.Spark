using System.Net;

namespace CodeCoverage;

/// <summary>
/// Builds the HTML message of a confirmation (<c>IRetryAccessor.Action</c>'s message is HTML, #554, owner 2026-11-06:
/// "&lt;br&gt; and &lt;li&gt; tags", values encoded "htmlentities-like"). Only this class writes markup; every text that
/// goes in, translations included, passes through <see cref="Encode"/> first.
/// </summary>
internal static class HtmlMessage
{
    public static string Encode(string text) => WebUtility.HtmlEncode(text);

    /// <summary>A <c>&lt;ul&gt;</c> of items that are already HTML (encoded text, or a nested list).</summary>
    public static string List(IEnumerable<string> itemsHtml)
        => "<ul>" + string.Concat(itemsHtml.Select(item => "<li>" + item + "</li>")) + "</ul>";

    /// <summary>An encoded heading over a list of encoded items.</summary>
    public static string Section(string heading, IEnumerable<string> items) => Encode(heading) + List(items.Select(Encode));

    /// <summary>An encoded line, ended with a <c>&lt;br&gt;</c>.</summary>
    public static string Line(string text) => Encode(text) + "<br>";
}
