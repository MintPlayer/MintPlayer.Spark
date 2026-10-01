using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace MintPlayer.Spark.IdentityProvider.Endpoints;

/// <summary>
/// Light/dark theming for the server-rendered <c>/connect</c> pages (#462, PRD D10/G3). These
/// pages ship no script, so the theme is decided on the server from the same
/// <c>bs-theme-mode</c> cookie the Spark SPA's theme toggle writes:
/// <list type="bullet">
///   <item>An explicit <c>light</c> or <c>dark</c> renders <c>&lt;html data-bs-theme="…"&gt;</c>.</item>
///   <item><c>auto</c>, no cookie, or anything else renders no attribute, and the
///   <c>@media (prefers-color-scheme: dark)</c> block in <see cref="Css"/> follows the OS, live.</item>
/// </list>
/// The cookie is host-only by default (G4), so it reaches these pages only when they share the
/// SPA's host (or the app set a <c>cookieDomain</c>); otherwise they follow the OS.
/// <para>
/// The cookie value is attacker-controlled input. It is never written into the page: the
/// attribute is one of two literal strings, chosen by an exact match.
/// </para>
/// </summary>
internal static partial class ConnectPageTheme
{
    public const string CookieName = "bs-theme-mode";

    // The same shape the ng-bootstrap theme store and pre-boot script accept; only light/dark
    // are meaningful here, since a custom variant has no stylesheet on these pages.
    [GeneratedRegex("^[a-z0-9-]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidMode();

    /// <summary><c>"light"</c> or <c>"dark"</c> for an explicit choice; null for auto, absent or invalid.</summary>
    public static string? ExplicitTheme(HttpRequest request)
    {
        var value = request.Cookies[CookieName];
        if (value is null || !ValidMode().IsMatch(value)) return null;
        return value switch
        {
            "light" => "light",
            "dark" => "dark",
            _ => null,
        };
    }

    // Bootstrap 5.3's dark palette, as custom properties. One copy, used twice below: for an
    // explicit dark choice and for auto under a dark OS.
    private const string DarkTokens =
        "color-scheme:dark;" +
        "--idp-bg:#212529;--idp-color:#dee2e6;--idp-heading:#f8f9fa;--idp-muted:#adb5bd;--idp-border:#495057;" +
        "--idp-input-bg:#212529;--idp-input-border:#495057;--idp-focus-border:#86b7fe;--idp-focus-ring:rgba(13,110,253,.25);" +
        "--idp-primary:#0d6efd;--idp-primary-hover:#0b5ed7;--idp-secondary:#6c757d;--idp-danger:#dc3545;--idp-link:#6ea8fe;" +
        "--idp-error-color:#ea868f;--idp-error-bg:#2c0b0e;--idp-error-border:#842029;" +
        "--idp-info-color:#6ea8fe;--idp-info-bg:#031633;--idp-info-border:#084298;" +
        "--idp-notice-color:#75b798;--idp-notice-bg:#051b11;--idp-warning-color:#ffda6a;--idp-warning-bg:#332701;";

    /// <summary>
    /// The shared stylesheet: light tokens on <c>:root</c>, the dark tokens for
    /// <c>[data-bs-theme=dark]</c> and for auto under a dark OS, and the base body colours.
    /// Each page appends its own layout rules, written against the <c>--idp-*</c> tokens.
    /// </summary>
    public const string Css =
        ":root{color-scheme:light dark;" +
        "--idp-bg:#fff;--idp-color:#212529;--idp-heading:#333;--idp-muted:#666;--idp-border:#eee;" +
        "--idp-input-bg:#fff;--idp-input-border:#ced4da;--idp-focus-border:#86b7fe;--idp-focus-ring:rgba(13,110,253,.25);" +
        "--idp-primary:#0d6efd;--idp-primary-hover:#0b5ed7;--idp-secondary:#6c757d;--idp-danger:#dc3545;--idp-link:#0d6efd;" +
        "--idp-error-color:#dc3545;--idp-error-bg:#f8d7da;--idp-error-border:#f5c2c7;" +
        "--idp-info-color:#084298;--idp-info-bg:#cfe2ff;--idp-info-border:#b6d4fe;" +
        "--idp-notice-color:#0f5132;--idp-notice-bg:#d1e7dd;--idp-warning-color:#856404;--idp-warning-bg:#fff3cd}" +
        ":root[data-bs-theme=light]{color-scheme:light}" +
        ":root[data-bs-theme=dark]{" + DarkTokens + "}" +
        "@media (prefers-color-scheme: dark){:root:not([data-bs-theme=light]){" + DarkTokens + "}}" +
        "body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;background:var(--idp-bg);color:var(--idp-color)}" +
        "h2{color:var(--idp-heading)}" +
        "input{background:var(--idp-input-bg);color:var(--idp-color)}";

    /// <summary>
    /// Opens the document through <c>&lt;style&gt;</c> + <see cref="Css"/>: the caller appends its
    /// page rules, then <c>&lt;/style&gt;&lt;/head&gt;&lt;body&gt;</c>. <paramref name="title"/> is
    /// raw text and is HTML-encoded here.
    /// </summary>
    public static void AppendDocumentStart(StringBuilder sb, HttpContext context, string title)
    {
        sb.Append("<!DOCTYPE html><html");
        var theme = ExplicitTheme(context.Request);
        if (theme is not null) sb.Append(" data-bs-theme=\"").Append(theme).Append('"');
        sb.Append("><head><meta charset=\"utf-8\"><meta name=\"color-scheme\" content=\"light dark\">");
        sb.Append("<title>").Append(ConnectPage.Encode(title)).Append("</title>");
        sb.Append("<style>").Append(Css);
    }
}
