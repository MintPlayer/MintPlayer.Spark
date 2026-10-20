namespace MintPlayer.Spark.Replication.Abstractions;

/// <summary>
/// The source path of a <c>[Replicated]</c> class's property, when it differs from the property's name:
/// a rename (<c>[ReplicatedFrom("Plate")]</c>) or a nested member (<c>[ReplicatedFrom("Owner.Name")]</c>).
/// The generated script reads nested paths null-safely, so a missing <c>Owner</c> replicates as
/// <see langword="null"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class ReplicatedFromAttribute : Attribute
{
    /// <param name="path">The dotted source path; every segment must be a plain identifier (SPARK066).</param>
    public ReplicatedFromAttribute(string path)
    {
        Path = path;
    }

    /// <summary>The dotted source path, e.g. <c>Owner.Name</c>.</summary>
    public string Path { get; }
}
