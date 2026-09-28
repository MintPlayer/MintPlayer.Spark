namespace MintPlayer.Spark.SoftDelete;

/// <summary>
/// Per-request hand-off between <see cref="SparkSoftDelete"/> and <see cref="SoftDeleteInterceptor"/>:
/// the reason a delete should record, and the ids a purge actually removed.
/// </summary>
/// <remarks>
/// A purge through <c>IDatabaseAccess</c> returns without an error when the id names nothing, or a
/// document of another collection (so it is no existence oracle). The service still has to know
/// whether it purged anything, and only the interceptor's after-hook — which runs only when the
/// delete really happened — can tell it.
/// </remarks>
internal sealed class SoftDeleteRequestState
{
    public string? PendingReason { get; set; }

    public HashSet<string> Purged { get; } = new(StringComparer.OrdinalIgnoreCase);
}
