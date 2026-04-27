using Microsoft.AspNetCore.Http;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// RequestCultureResolver implements RFC 9110 Accept-Language quality-factor parsing,
/// supports base-culture fallback ("en-US" → "en" when only "en" is configured), and falls
/// back to the configured DefaultLanguage when nothing matches. Pins the parsing edge cases
/// because malformed Accept-Language headers from the wild shouldn't crash the request.
/// </summary>
public class RequestCultureResolverTests
{
    private readonly IHttpContextAccessor _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
    private readonly ICultureLoader _cultureLoader = Substitute.For<ICultureLoader>();

    private RequestCultureResolver CreateResolver(
        Dictionary<string, TranslatedString> languages,
        string? acceptLanguage,
        string defaultLanguage = "en")
    {
        _cultureLoader.GetCulture().Returns(new CultureConfiguration
        {
            DefaultLanguage = defaultLanguage,
            Languages = languages,
        });

        if (acceptLanguage is null)
        {
            _httpContextAccessor.HttpContext.Returns((HttpContext?)null);
        }
        else
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Headers.AcceptLanguage = acceptLanguage;
            _httpContextAccessor.HttpContext.Returns(ctx);
        }

        return new RequestCultureResolver(_httpContextAccessor, _cultureLoader);
    }

    private static Dictionary<string, TranslatedString> Cultures(params string[] keys) =>
        keys.ToDictionary(k => k, k => TranslatedString.Create(k));

    [Fact]
    public void Returns_default_when_no_HttpContext()
    {
        var resolver = CreateResolver(Cultures("en", "nl"), acceptLanguage: null, defaultLanguage: "en");

        resolver.GetCurrentCulture().Should().Be("en");
    }

    [Fact]
    public void Returns_default_when_AcceptLanguage_header_is_missing()
    {
        var resolver = CreateResolver(Cultures("en", "nl"), acceptLanguage: "", defaultLanguage: "en");

        resolver.GetCurrentCulture().Should().Be("en");
    }

    [Fact]
    public void Returns_supported_language_when_AcceptLanguage_matches_directly()
    {
        var resolver = CreateResolver(Cultures("en", "nl"), acceptLanguage: "nl");

        resolver.GetCurrentCulture().Should().Be("nl");
    }

    [Fact]
    public void Falls_back_to_base_culture_when_only_base_is_configured()
    {
        // "en-US" not configured, but "en" is — should return "en".
        var resolver = CreateResolver(Cultures("en", "nl"), acceptLanguage: "en-US");

        resolver.GetCurrentCulture().Should().Be("en");
    }

    [Fact]
    public void Returns_default_when_neither_specific_nor_base_culture_is_configured()
    {
        var resolver = CreateResolver(Cultures("en", "nl"), acceptLanguage: "fr-FR", defaultLanguage: "en");

        resolver.GetCurrentCulture().Should().Be("en");
    }

    [Fact]
    public void Picks_highest_quality_factor_among_multiple_languages()
    {
        // de has highest quality, but isn't supported. nl is supported with q=0.5, en with q=0.8.
        var resolver = CreateResolver(
            Cultures("en", "nl"),
            acceptLanguage: "de;q=1.0, nl;q=0.5, en;q=0.8");

        resolver.GetCurrentCulture().Should().Be("en");
    }

    [Fact]
    public void Default_quality_when_omitted_is_treated_as_1_0()
    {
        // No q= → quality 1.0 → highest. nl is configured.
        var resolver = CreateResolver(
            Cultures("en", "nl"),
            acceptLanguage: "nl, en;q=0.5");

        resolver.GetCurrentCulture().Should().Be("nl");
    }

    [Fact]
    public void Tolerates_whitespace_in_accept_language_entries()
    {
        var resolver = CreateResolver(Cultures("en", "nl"), acceptLanguage: "  nl ; q=0.9 ,  en ; q=0.5  ");

        resolver.GetCurrentCulture().Should().Be("nl");
    }

    [Fact]
    public void Returns_first_supported_match_walking_quality_descending()
    {
        // First in q-desc order: fr (q=1.0) — not supported.
        // Next: nl (q=0.9) — supported. Wins. en (q=0.8) is not consulted.
        var resolver = CreateResolver(
            Cultures("en", "nl"),
            acceptLanguage: "fr;q=1.0, nl;q=0.9, en;q=0.8");

        resolver.GetCurrentCulture().Should().Be("nl");
    }
}
