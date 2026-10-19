using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Embed;

namespace MintPlayer.Spark.Extensions;

/// <summary>
/// Embed mode (<c>docs/commerce_PRD.md</c> X23, <c>docs/guide-embed.md</c>):
/// <code>
/// spark.AddEmbed(o => o.AllowedOrigins.Add("https://organiser.example"));
/// </code>
/// The embed routes (<c>/embed/…</c> by default) may then be framed by the listed origins, every other page only by the
/// platform itself, and the organiser's loader script is served at <c>/spark/embed/loader.js</c>.
/// </summary>
public static class SparkBuilderEmbedExtensions
{
    /// <summary>Switches embed mode on and applies <paramref name="configure"/> over <c>Spark:Embed</c> (code wins).</summary>
    public static ISparkBuilder AddEmbed(this ISparkBuilder builder, Action<SparkEmbedOptions>? configure = null)
    {
        builder.Services.Configure<SparkEmbedOptions>(o =>
        {
            o.Enabled = true;
            configure?.Invoke(o);
        });
        return builder;
    }
}
