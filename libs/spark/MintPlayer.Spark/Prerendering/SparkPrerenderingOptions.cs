using MintPlayer.Spark.Abstractions.Prerendering;

namespace MintPlayer.Spark.Prerendering;

/// <summary>Server-side rendering (docs/spark_ssr_PRD.md), set through <c>AddPrerendering(...)</c>.</summary>
public sealed class SparkPrerenderingOptions
{
    /// <summary>
    /// The zone dates render in for a visitor without the timezone cookie, i.e. on a first visit (PRD D10).
    /// An IANA id. The browser re-formats after hydration if its own zone differs.
    /// </summary>
    public string DefaultTimeZone { get; set; } = "UTC";

    internal List<(PathString Path, Func<ISparkPrerenderContext, Task> Supply)> Pages { get; } = [];

    /// <summary>
    /// Supplies an application page Spark's routes do not cover, e.g. a board on <c>/home</c>
    /// (PRD D11). <paramref name="path"/> matches exactly, ignoring case.
    /// </summary>
    public SparkPrerenderingOptions MapPage(PathString path, Func<ISparkPrerenderContext, Task> supply)
    {
        Pages.Add((path, supply));
        return this;
    }
}
