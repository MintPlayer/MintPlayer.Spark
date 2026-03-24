namespace MintPlayer.Spark.Configuration;

public class SparkOptions
{
    public RavenDbOptions RavenDb { get; set; } = new();
}

public class RavenDbOptions
{
    public string[] Urls { get; set; } = ["http://localhost:8080"];
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
