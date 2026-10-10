using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Http;

namespace MintPlayer.Spark.Extensions;

/// <summary>
/// The application's SSRF-safe HTTP client (<c>docs/seo_analyzer_PRD.md</c> X2), for fetching URLs that
/// users supply:
/// <code>
/// builder.Services.AddSpark(builder.Configuration, spark => spark.AddSafeHttp(o => o.MaxResponseBytes = 2_000_000));
/// var client = httpClientFactory.CreateClient(SparkBuilderSafeHttpExtensions.ClientName);
/// </code>
/// A library that needs its own limits registers its own name with
/// <see cref="SafeHttpServiceCollectionExtensions.AddSafeHttpClient"/>, as the identity provider does.
/// </summary>
public static class SparkBuilderSafeHttpExtensions
{
    /// <summary>The name of the client <see cref="AddSafeHttp"/> registers.</summary>
    public const string ClientName = "Spark.SafeHttp";

    private const string ConfigurationSection = "Spark:SafeHttp";

    /// <summary>
    /// Registers the named client <see cref="ClientName"/>. <c>Spark:SafeHttp</c> is bound first and
    /// <paramref name="configure"/> runs second: code wins over configuration, as everywhere in Spark.
    /// </summary>
    public static ISparkBuilder AddSafeHttp(this ISparkBuilder builder, Action<SafeHttpOptions>? configure = null)
    {
        builder.Services.AddSafeHttpClient(ClientName, builder.Configuration?.GetSection(ConfigurationSection), configure);
        return builder;
    }
}
