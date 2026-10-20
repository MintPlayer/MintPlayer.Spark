namespace MintPlayer.Spark.Replication.Abstractions;

/// <summary>
/// The <c>@metadata</c> keys the replication script stamps on every replica.
/// </summary>
/// <remarks>
/// Also linked into <c>MintPlayer.Spark.LibraryGenerators</c> with the renderer, so it must stay
/// netstandard2.0-compatible.
/// </remarks>
public static class ReplicaMetadata
{
    /// <summary>
    /// The change vector of the source document the replica was built from. A write-back sends it as the
    /// owner's base change vector, so a stale edit is a conflict (WD18).
    /// </summary>
    public const string SourceChangeVector = "Spark-Replica-Source-Change-Vector";

    /// <summary>The module the replica came from; only an ETL script deployed for one module carries it.</summary>
    public const string SourceModule = "Spark-Replica-Source-Module";
}
