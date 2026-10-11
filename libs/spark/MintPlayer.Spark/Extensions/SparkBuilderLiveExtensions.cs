using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Live;

namespace MintPlayer.Spark.Extensions;

/// <summary>
/// Live updates (X4, <c>docs/guide-live-updates.md</c>). Always on: <c>"live": true</c> on a type or query in
/// the model JSON is all an application writes (D34). This call is only for code options:
/// <code>
/// builder.Services.AddSpark(builder.Configuration, spark => spark.AddLive(o => o.Source = SparkLiveSource.Actions));
/// </code>
/// Configuration (<c>Spark:Live</c>) does the same without code.
/// </summary>
public static class SparkBuilderLiveExtensions
{
    /// <summary>Applies <paramref name="configure"/> over <c>Spark:Live</c> (code wins).</summary>
    public static ISparkBuilder AddLive(this ISparkBuilder builder, Action<SparkLiveOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        builder.Services.Configure(configure);
        return builder;
    }
}
