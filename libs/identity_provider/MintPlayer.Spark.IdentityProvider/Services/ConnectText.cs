using Microsoft.AspNetCore.Http;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// The language of the server-rendered <c>/connect/*</c> pages (<c>docs/identity_provider_platform_PRD.md</c>
/// D7), and their texts, from the same translations the SPA uses (<c>identityProvider.connect.*</c>).
/// <para>
/// The language is, in order: the authorize request's <c>ui_locales</c>, the <c>spark-lang</c> cookie
/// the SPA writes when the user picks a language, then the browser's <c>Accept-Language</c>; each only
/// when the application supports it (<c>culture.json</c>).
/// </para>
/// </summary>
internal sealed class ConnectText(
    IHttpContextAccessor httpContextAccessor,
    IManager manager,
    ICultureLoader cultureLoader,
    IRequestCultureResolver requestCulture)
{
    /// <summary>The cookie ng-spark's language service writes beside its localStorage entry.</summary>
    public const string CultureCookie = "spark-lang";

    private string? culture;
    private string? uiLocales;

    /// <summary>Records the request's <c>ui_locales</c>, which wins over everything else.</summary>
    public void UseUiLocales(string? value)
    {
        uiLocales = value;
        culture = null;
    }

    /// <summary>
    /// For a page that only knows the <c>returnUrl</c> it will resume (sign-in, two-factor): when that is the
    /// pending <c>/connect/authorize</c> request, its <c>ui_locales</c> is the client's language request.
    /// The value only selects among supported cultures, so the untrusted URL cannot inject anything.
    /// </summary>
    public void UseUiLocalesOfReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl)) return;
        var queryStart = returnUrl.IndexOf('?');
        if (queryStart < 0 || !returnUrl.AsSpan(0, queryStart).Equals("/connect/authorize", StringComparison.OrdinalIgnoreCase))
            return;
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(returnUrl[queryStart..]);
        if (query.TryGetValue("ui_locales", out var value) && !string.IsNullOrEmpty(value.ToString()))
            UseUiLocales(value.ToString());
    }

    /// <summary>The page's language, for <c>&lt;html lang&gt;</c> and every text.</summary>
    public string Culture => culture ??= Resolve();

    /// <summary>The text for <c>identityProvider.connect.<paramref name="key"/></c>, formatted with <paramref name="args"/>.</summary>
    public string this[string key, params object[] args] => manager.GetMessage("identityProvider.connect." + key, Culture, args);

    /// <summary>A translated value in the page's language, or <paramref name="fallback"/>.</summary>
    public string Of(TranslatedString? value, string fallback)
        => value?.GetValue(Culture) is { Length: > 0 } text ? text : fallback;

    private string Resolve()
    {
        var supported = cultureLoader.GetCulture().Languages.Keys;
        foreach (var candidate in (uiLocales ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
                     .Append(httpContextAccessor.HttpContext?.Request.Cookies[CultureCookie] ?? ""))
        {
            if (Supported(candidate, supported) is { } match)
                return match;
        }
        return requestCulture.GetCurrentCulture();
    }

    private static string? Supported(string candidate, IEnumerable<string> supported)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        var list = supported.ToList();
        if (list.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            return list.First(s => string.Equals(s, candidate, StringComparison.OrdinalIgnoreCase));
        var baseCulture = candidate.Split('-')[0];
        return list.FirstOrDefault(s => string.Equals(s, baseCulture, StringComparison.OrdinalIgnoreCase));
    }
}
