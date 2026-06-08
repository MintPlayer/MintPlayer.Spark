namespace MintPlayer.Spark.Configuration;

public class SparkOptions
{
    public RavenDbOptions RavenDb { get; set; } = new();
    public BreadcrumbOptions Breadcrumb { get; set; } = new();
}

public class BreadcrumbOptions
{
    /// <summary>
    /// Maximum reference depth the breadcrumb resolver follows (roots are depth 0). Bounds the
    /// number of batched load round-trips and breaks reference cycles. Default: 5.
    /// </summary>
    public int MaxDepth { get; set; } = 5;

    /// <summary>Separator joining the breadcrumbs of a reference-array placeholder. Default: ", ".</summary>
    public string ReferenceSeparator { get; set; } = ", ";

    /// <summary>
    /// Rendered in place of a referenced entity the current user is not allowed to read
    /// (row-level Read denied). Default: "—".
    /// </summary>
    public string RedactedPlaceholder { get; set; } = "—";
}

public class RavenDbOptions
{
    /// <summary>
    /// RavenDB cluster URLs. Empty means "use the default" — see <see cref="SparkExtensions"/>
    /// where an unset value falls back to <c>http://localhost:8080</c>. The default must stay
    /// empty so <see cref="Microsoft.Extensions.Configuration.ConfigurationBinder"/> can replace
    /// it: array binding APPENDS config values to an existing array rather than overwriting,
    /// so a non-empty default would prevent <c>appsettings.{env}.json</c> or env-var overrides
    /// from taking effect for the first URL.
    /// </summary>
    public string[] Urls { get; set; } = [];
    public string Database { get; set; } = "Spark";

    /// <summary>
    /// Maximum number of connection attempts when waiting for RavenDB to become available.
    /// Set to 0 to disable retry logic. Default: 30.
    /// </summary>
    public int MaxConnectionRetries { get; set; } = 30;

    /// <summary>
    /// Delay in seconds between connection retry attempts. Default: 2.
    /// </summary>
    public int RetryDelaySeconds { get; set; } = 2;

    /// <summary>
    /// Automatically create the database if it does not exist.
    /// Always enabled in Development mode. Set to true for container deployments
    /// where the database may not exist yet. Default: false.
    /// </summary>
    public bool EnsureDatabaseCreated { get; set; } = false;
}
