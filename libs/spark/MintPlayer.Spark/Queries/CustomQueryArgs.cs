using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Queries;

/// <summary>
/// Context passed to a custom query method when executed.
/// </summary>
public sealed class CustomQueryArgs
{
    /// <summary>
    /// The parent PersistentObject (for detail/sub-queries).
    /// Null for top-level queries.
    /// </summary>
    public PersistentObject? Parent { get; set; }

    /// <summary>
    /// The entity type name of the parent (e.g., "Company").
    /// </summary>
    public string? ParentType { get; set; }

    /// <summary>
    /// The SparkQuery being executed (for conditional behavior based on query metadata).
    /// </summary>
    public required SparkQuery Query { get; set; }
}
