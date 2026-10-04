namespace MintPlayer.Spark.History;

/// <summary>
/// Per-request hand-off inside the History package: the revision a revert is restoring (so the
/// interceptor can make its values exact).
/// </summary>
internal sealed class HistoryRequestState
{
    /// <summary>The revision entity <see cref="SparkHistory.RevertAsync"/> is saving back; null otherwise.</summary>
    public object? RevertSource { get; set; }

    /// <summary>
    /// Set by the interceptor when the revert in flight could not restore an attribute the caller may
    /// not edit (a static attribute right) whose revision value differs (contributions M2c-2b).
    /// </summary>
    public bool RevertPartial { get; set; }
}
