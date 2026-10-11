using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Sharing;

namespace MintPlayer.Spark.Extensions;

/// <summary>
/// Share links (<c>docs/seo_analyzer_PRD.md</c> X6):
/// <code>
/// spark.AddShareLinks(o => o.Share&lt;Site&gt;(
///     attributes: ["Origin", "LatestScore", "LatestGrade"],
///     queries: new Dictionary&lt;string, string[]&gt; { ["SiteRuns"] = ["StartedAtUtc", "Score", "Grade"] }));
/// // security.json: grant ShareLink/Site and RevokeShareLink/Site (with Read/Site) to who may share.
/// </code>
/// </summary>
public static class SparkBuilderShareExtensions
{
    /// <summary>
    /// Declares the shareable types and what their links show (<c>Spark:Share</c> first, then <paramref name="configure"/>).
    /// The <c>ShareLink</c> / <c>RevokeShareLink</c> actions, the anonymous <c>/spark/share</c> endpoints and the
    /// protection of <c>ShareTokenHash</c> are part of core; without a declaration nothing can be shared.
    /// </summary>
    public static ISparkBuilder AddShareLinks(this ISparkBuilder builder, Action<SparkShareOptions> configure)
    {
        builder.Services.Configure(configure);
        return builder;
    }
}
