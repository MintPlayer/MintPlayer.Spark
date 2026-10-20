namespace MintPlayer.Spark.Replication.Abstractions;

/// <summary>
/// Lists an assembly's <c>[Replicated]</c> types, so the runtime finds them without scanning members.
/// Generated once per compilation by <c>MintPlayer.Spark.LibraryGenerators</c>, never written by hand; a
/// type with a replication diagnostic is left out.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class SparkReplicaTypesAttribute(params Type[] types) : Attribute
{
    /// <summary>The replica types, sorted by full name.</summary>
    public Type[] Types { get; } = types;
}
