using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Sitemap;

namespace MintPlayer.Spark.Extensions;

/// <summary>
/// Discovery output (<c>docs/commerce_PRD.md</c> X22, <c>docs/guide-page-meta.md</c> "Sitemap, JSON-LD and hreflang"):
/// <code>
/// spark.AddSitemap(o => o.RobotsTxt = true);
/// spark.AddSitemapSource&lt;EventSitemap&gt;();   // Name => "events"
/// </code>
/// <c>/sitemap.xml</c> is mapped by <c>MapSpark()</c> once a source is registered.
/// </summary>
public static class SparkBuilderSitemapExtensions
{
    /// <summary>Applies <paramref name="configure"/> over <c>Spark:Sitemap</c> (code wins).</summary>
    public static ISparkBuilder AddSitemap(this ISparkBuilder builder, Action<SparkSitemapOptions> configure)
    {
        builder.Services.Configure(configure);
        return builder;
    }

    /// <summary>Registers <typeparamref name="TSource"/> (scoped; idempotent per type).</summary>
    public static ISparkBuilder AddSitemapSource<TSource>(this ISparkBuilder builder)
        where TSource : class, ISparkSitemapSource
    {
        builder.Services.TryAddSingleton<SparkSitemapMarker>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<ISparkSitemapSource, TSource>());
        return builder;
    }
}
