namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Wrapper for the App_Data/Model/*.json file format.
/// Contains the entity type definition and its associated queries.
/// </summary>
public sealed class EntityTypeFile
{
    public required EntityTypeDefinition PersistentObject { get; set; }
    public SparkQuery[] Queries { get; set; } = [];
}
