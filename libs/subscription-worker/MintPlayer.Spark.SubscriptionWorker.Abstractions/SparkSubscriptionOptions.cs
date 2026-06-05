namespace MintPlayer.Spark.SubscriptionWorker;

public class SparkSubscriptionOptions
{
    /// <summary>
    /// Whether to wait for non-stale indexes before starting workers.
    /// </summary>
    public bool WaitForNonStaleIndexes { get; set; } = true;

    /// <summary>
    /// Timeout for waiting for non-stale indexes.
    /// </summary>
    public TimeSpan NonStaleIndexTimeout { get; set; } = TimeSpan.FromMinutes(2);
}
