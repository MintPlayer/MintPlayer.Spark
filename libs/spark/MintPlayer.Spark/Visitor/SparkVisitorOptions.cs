namespace MintPlayer.Spark.Visitor;

/// <summary>
/// The visitor cookie (commerce PRD X1, <c>docs/guide-controllers.md</c> "Visitors"), bound from
/// <c>Spark:Visitor</c>.
/// </summary>
public sealed class SparkVisitorOptions
{
    /// <summary>The configuration section these options are bound from.</summary>
    public const string SectionName = "Spark:Visitor";

    /// <summary>The cookie's name. Default <c>.Spark.Visitor</c>.</summary>
    public string CookieName { get; set; } = ".Spark.Visitor";

    /// <summary>
    /// How long a visitor cookie lives after it was issued, and how long a merge is remembered. A cookie
    /// older than this is treated as absent even when a browser still sends it. Default 180 days.
    /// </summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromDays(180);
}
