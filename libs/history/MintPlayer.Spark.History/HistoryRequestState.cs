using MintPlayer.Spark.Abstractions.Interceptors;

namespace MintPlayer.Spark.History;

/// <summary>
/// Per-request hand-off inside the History package: the revision a revert is restoring (so the
/// interceptor can make its values exact), and each in-flight write's previous change vector (read in
/// the before-hook, reported in the after-hook).
/// </summary>
internal sealed class HistoryRequestState
{
    /// <summary>The revision entity <see cref="SparkHistory.RevertAsync"/> is saving back; null otherwise.</summary>
    public object? RevertSource { get; set; }

    private readonly Dictionary<PersistentObjectInterceptorContext, string?> previous = new(ReferenceEqualityComparer.Instance);

    public void SetPrevious(PersistentObjectInterceptorContext context, string? changeVector) => previous[context] = changeVector;

    public string? TakePrevious(PersistentObjectInterceptorContext context)
        => previous.Remove(context, out var changeVector) ? changeVector : null;
}
