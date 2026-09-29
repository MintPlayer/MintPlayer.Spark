namespace MintPlayer.Spark.SoftDelete;

/// <summary>Options for soft deletion, bound from <c>Spark:SoftDelete</c> (code wins).</summary>
public sealed class SparkSoftDeleteOptions
{
    /// <summary>
    /// Whether the system context (module sync, background jobs) is also kept away from deleted rows.
    /// Default <c>true</c>: a background job that sends reminders must not send them about a deleted
    /// order. Set <c>false</c> for jobs that legitimately work on deleted rows (an automatic purge of
    /// rows deleted more than N days ago); such a job then sees deleted and live rows alike.
    /// </summary>
    public bool ApplyInSystemContext { get; set; } = true;
}
