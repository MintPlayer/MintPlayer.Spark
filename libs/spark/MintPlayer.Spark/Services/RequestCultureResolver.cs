using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Services;

public interface IRequestCultureResolver
{
    string GetCurrentCulture();
}

[Register(typeof(IRequestCultureResolver), ServiceLifetime.Scoped)]
internal partial class RequestCultureResolver : IRequestCultureResolver
{
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;
    [Inject] private readonly ICultureLoader cultureLoader;

    public string GetCurrentCulture()
    {
        var cultureConfig = cultureLoader.GetCulture();
        var supportedCultures = cultureConfig.Languages.Keys;

        var acceptLanguage = httpContextAccessor.HttpContext?.Request.Headers.AcceptLanguage.ToString();
        if (!string.IsNullOrEmpty(acceptLanguage))
        {
            var languages = acceptLanguage
                .Split(',')
                .Select(ParseLanguageEntry)
                .OrderByDescending(e => e.Quality)
                .Select(e => e.Language);

            // RFC 4647 lookup, the chain TranslatedString resolves with (commerce X17):
            // zh-Hant-TW → zh-Hant → zh; tags compare case-insensitively.
            foreach (var lang in languages)
                foreach (var candidate in LanguageTag.LookupChain(lang))
                    if (supportedCultures.FirstOrDefault(c => LanguageTag.AreEqual(c, candidate)) is { } supported)
                        return supported;
        }

        return cultureConfig.DefaultLanguage;
    }

    private static (string Language, double Quality) ParseLanguageEntry(string entry)
    {
        var parts = entry.Trim().Split(';');
        var language = parts[0].Trim();
        var quality = 1.0;

        if (parts.Length > 1)
        {
            var qPart = parts[1].Trim();
            if (qPart.StartsWith("q=") && double.TryParse(qPart[2..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var q))
                quality = q;
        }

        return (language, quality);
    }
}
