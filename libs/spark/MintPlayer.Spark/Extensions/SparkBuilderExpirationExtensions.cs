using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Expiration;

namespace MintPlayer.Spark.Extensions;

/// <summary>
/// Document expiration (<c>docs/seo_analyzer_PRD.md</c> X1):
/// <code>
/// builder.Services.AddSpark(builder.Configuration, spark => spark.AddExpiration());
///
/// public class PageResult : IExpiring { public DateTime? ExpiresAtUtc { get; set; } … }
/// // App_Data/Model/PageResult.json: "persistentObject": { …, "expiresAfter": "7.00:00:00" }
/// </code>
/// Stamping <c>@expires</c> and hiding expired rows are always on for <see cref="Abstractions.IExpiring"/>
/// types, and a model with one enables expiration on the database by itself; this call is for code
/// options and for an app that writes <c>@expires</c> by hand.
/// </summary>
public static class SparkBuilderExpirationExtensions
{
    /// <summary>
    /// Requires RavenDB document expiration on the database and applies <paramref name="configure"/> over
    /// <c>Spark:Expiration</c> (code wins). Idempotent; safe to call from several libraries.
    /// </summary>
    public static ISparkBuilder AddExpiration(this ISparkBuilder builder, Action<SparkExpirationOptions>? configure = null)
    {
        if (configure is not null)
            builder.Services.Configure(configure);
        builder.Registry.RequireDocumentExpiration("AddExpiration()");
        return builder;
    }
}
