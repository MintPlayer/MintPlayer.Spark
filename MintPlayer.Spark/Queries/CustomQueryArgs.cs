using MintPlayer.Spark.Abstractions;
using Raven.Client.Documents.Session;

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
    /// The SparkQuery being executed (for conditional behavior based on query metadata).
    /// </summary>
    public required SparkQuery Query { get; set; }

    /// <summary>
    /// The RavenDB async document session for database access.
    /// </summary>
    public required IAsyncDocumentSession Session { get; set; }

    /// <summary>
    /// Validates that a parent is present and of the expected type.
    /// Throws InvalidOperationException if the parent is missing or wrong type.
    /// </summary>
    public void EnsureParent(string expectedTypeName)
    {
        if (Parent is null)
            throw new InvalidOperationException(
                $"Custom query '{Query.Name}' requires a parent object.");
        if (!string.Equals(Parent.Name, expectedTypeName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Custom query '{Query.Name}' expects parent of type '{expectedTypeName}', got '{Parent.Name}'.");
    }

    /// <summary>
    /// Validates that a parent is present and one of the expected types.
    /// </summary>
    public void EnsureParent(params string[] expectedTypeNames)
    {
        if (Parent is null)
            throw new InvalidOperationException(
                $"Custom query '{Query.Name}' requires a parent object.");
        if (!expectedTypeNames.Any(t => string.Equals(Parent.Name, t, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                $"Custom query '{Query.Name}' expects parent of type [{string.Join(", ", expectedTypeNames)}], got '{Parent.Name}'.");
    }
}
