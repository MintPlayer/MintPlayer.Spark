namespace MintPlayer.Spark.Expiration;

/// <summary>
/// Document expiration (<c>docs/seo_analyzer_PRD.md</c> X1), bound from <c>Spark:Expiration</c>, then
/// <c>spark.AddExpiration(o =&gt; …)</c> (code wins).
/// </summary>
public sealed class SparkExpirationOptions
{
    /// <summary>The configuration section these options are bound from.</summary>
    public const string SectionName = "Spark:Expiration";

    /// <summary>
    /// How often RavenDB sweeps expired documents. Default and minimum
    /// <see cref="SparkExpiration.MinimumDeleteFrequency"/> (36 hours): the Community licence refuses
    /// anything more frequent, and startup refuses it on every licence so a Developer licence in CI
    /// cannot hide the mistake.
    /// </summary>
    public TimeSpan DeleteFrequency { get; set; } = SparkExpiration.MinimumDeleteFrequency;

    /// <summary>
    /// Whether <c>UseSpark()</c> enables expiration on the database when something needs it. Turn it
    /// off when the operator manages the database record by hand (it is a database-admin operation).
    /// Stamping and the read filter are unaffected.
    /// </summary>
    public bool ConfigureDatabase { get; set; } = true;
}
