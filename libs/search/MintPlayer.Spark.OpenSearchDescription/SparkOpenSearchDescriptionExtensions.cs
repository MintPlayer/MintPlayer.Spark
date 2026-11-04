using MintPlayer.AspNetCore.OpenSearch;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Search;

namespace MintPlayer.Spark.OpenSearchDescription;

/// <summary>
/// <c>spark.AddOpenSearchDescription(o => o.ShortName = "...")</c> (<c>docs/spark_search_PRD.md</c> D9): the app becomes a
/// search engine a browser can add, through MintPlayer.AspNetCore.OpenSearch. Its three endpoints are mapped by
/// <c>MapSpark()</c> with the other modules' endpoints, so the SPA fallback never shadows them.
/// </summary>
public static class SparkOpenSearchDescriptionExtensions
{
    /// <summary>
    /// Serves the description, suggestions and search redirect. Adds <c>AddSearch()</c> when the app has not: call
    /// <c>AddSearch(...)</c> first to configure it.
    /// </summary>
    public static ISparkBuilder AddOpenSearchDescription(this ISparkBuilder builder, Action<SparkOpenSearchDescriptionOptions>? configure = null)
    {
        var options = new SparkOpenSearchDescriptionOptions();
        configure?.Invoke(options);

        if (!builder.Services.Any(d => d.ServiceType == typeof(ISparkSearch)))
            builder.AddSearch();

        builder.Services.AddSingleton(options);
        builder.Services.AddHttpContextAccessor();
        // The library registers MVC with an XML formatter that writes only its description document, and sets
        // RespectBrowserAcceptHeader; every other response still negotiates to JSON (PRD §1.2, owner: stays).
        builder.Services.AddOpenSearch<SparkOpenSearchService>(o =>
        {
            o.OsdxEndpoint = options.Path;
            o.SuggestUrl = options.SuggestPath;
            o.SearchUrl = options.SearchPath;
            o.SearchTermsParameter = "q";
            o.ShortName = options.ShortName is { Length: > SparkOpenSearchDescriptionOptions.ShortNameMaxLength } name
                ? name[..SparkOpenSearchDescriptionOptions.ShortNameMaxLength]
                : options.ShortName;
            o.Description = options.Description;
            o.Contact = options.Contact;
            o.ImageUrl = options.ImageUrl;
            o.ImageType = options.ImageType;
            o.ImageWidth = options.ImageSize;
            o.ImageHeight = options.ImageSize;
        });
        builder.Registry.AddEndpoints(endpoints =>
        {
            // A browser fetches these signed out: anonymous like the sitemap and /spark/search, so an app's fallback
            // authorization policy cannot lock them. A group, because MapOpenSearch takes no conventions itself.
            var group = endpoints.MapGroup("");
            group.AllowAnonymous().ExcludeFromDescription();
            group.MapOpenSearch();
        });
        return builder;
    }
}
