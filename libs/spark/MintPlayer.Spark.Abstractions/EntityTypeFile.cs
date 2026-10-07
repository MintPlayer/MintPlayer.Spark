namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Wrapper for the App_Data/Model/*.json file format.
/// Contains the entity type definition and its associated queries.
/// </summary>
public sealed class EntityTypeFile
{
    /// <summary>
    /// The entity type this file defines. In a file for a type a library ships, a delta: only what the
    /// application changes, matched by <c>name</c>.
    /// </summary>
    public required EntityTypeDefinition PersistentObject { get; set; }

    /// <summary>The queries over this type (lists, program-unit targets, sub-queries), matched across layers by <c>name</c>.</summary>
    public SparkQuery[] Queries { get; set; } = [];
}
