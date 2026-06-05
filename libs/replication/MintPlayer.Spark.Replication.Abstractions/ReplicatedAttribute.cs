namespace MintPlayer.Spark.Replication.Abstractions;

/// <summary>
/// Marks a class as a replicated entity whose data originates from another Spark module.
/// On startup, the framework collects all [Replicated] attributes, groups them by source module,
/// and sends the ETL scripts to the source module so it can create RavenDB ETL tasks
/// to push data into this module's database.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class ReplicatedAttribute : Attribute
{
    /// <summary>
    /// The name of the module that owns the original data.
    /// Must match the ModuleName in the source module's SparkReplicationOptions.
    /// </summary>
    public required string SourceModule { get; init; }

    /// <summary>
    /// The RavenDB collection name in the source database to replicate from.
    /// If null, inferred from <see cref="OriginalType"/> or the decorated class name.
    /// </summary>
    public string? SourceCollection { get; init; }

    /// <summary>
    /// The original CLR type in the source module.
    /// Used to infer the source collection name if <see cref="SourceCollection"/> is not set.
    /// </summary>
    public Type? OriginalType { get; init; }

    /// <summary>
    /// The JavaScript ETL transformation script.
    /// Uses RavenDB ETL script syntax (e.g. <c>loadToCars({...})</c>).
    /// </summary>
    public required string EtlScript { get; init; }
}
