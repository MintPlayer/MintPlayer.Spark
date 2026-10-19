using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.PageMeta;

namespace MintPlayer.Spark.Extensions;

/// <summary>
/// Server-side meta tags for SPA routes (<c>docs/commerce_PRD.md</c> X6, <c>docs/guide-page-meta.md</c>):
/// <code>
/// spark.AddPageMeta(o => o.PublicOrigin = "https://shop.example");
/// spark.AddPageMetaProvider&lt;EventPageMeta&gt;();   // RoutePatterns => ["/events/{slug}"]
/// </code>
/// The middleware is part of core and does nothing until a provider is registered.
/// </summary>
public static class SparkBuilderPageMetaExtensions
{
    /// <summary>Applies <paramref name="configure"/> over <c>Spark:PageMeta</c> (code wins).</summary>
    public static ISparkBuilder AddPageMeta(this ISparkBuilder builder, Action<SparkPageMetaOptions> configure)
    {
        builder.Services.Configure(configure);
        return builder;
    }

    /// <summary>
    /// Registers <typeparamref name="TProvider"/>'s public routes. Scoped: a provider may load from the request's session.
    /// Idempotent per provider type.
    /// </summary>
    public static ISparkBuilder AddPageMetaProvider<TProvider>(this ISparkBuilder builder)
        where TProvider : class, ISparkPageMetaProvider
    {
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<ISparkPageMetaProvider, TProvider>());
        return builder;
    }
}
