using Microsoft.AspNetCore.Http;
using MintPlayer.SourceGenerators.Attributes;

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

            foreach (var lang in languages)
            {
                if (supportedCultures.Contains(lang))
                    return lang;

                // Try base culture (e.g. "en" from "en-US")
                var baseCulture = lang.Split('-')[0];
                if (supportedCultures.Contains(baseCulture))
                    return baseCulture;
            }
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
