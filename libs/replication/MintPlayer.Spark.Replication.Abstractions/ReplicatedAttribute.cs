namespace MintPlayer.Spark.Replication.Abstractions;

/// <summary>
/// Marks a <c>partial</c> class as a read-only replica of another Spark module's documents. The class is
/// the single declaration of the replicated shape (warehouse WD13): <c>MintPlayer.Spark.LibraryGenerators</c>
/// generates its manifest, its ETL script and their hash into the class as constants (WD14), so nothing
/// else states the shape and nothing is reflected over at runtime.
/// <code>
/// [Replicated(SourceCollection = "Cars", SourceModule = "Fleet", WriteBack = true)]
/// public partial class Car
/// {
///     public string? Id { get; set; }
///     public string LicensePlate { get; set; } = "";
///     [ReplicatedFrom("Owner.Name")] public string? OwnerName { get; set; }
/// }
/// </code>
/// <para>
/// Every public instance property with a public setter or <c>init</c> accessor is mapped, except <c>Id</c>
/// and <c>[IgnoreProperty]</c> members. A new shape is a new replica class in a new target collection, so
/// versions run side by side without a version number.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ReplicatedAttribute : Attribute
{
    /// <summary>
    /// The owner's RavenDB collection the script reads, e.g. <c>Cars</c>. Never inferred: the generator
    /// cannot know the owner's naming conventions.
    /// </summary>
    public required string SourceCollection { get; init; }

    /// <summary>
    /// The module that owns the original documents. A generic library (e.g. <c>Fulfilment.Contracts</c>)
    /// leaves it out, and the host names its source modules in configuration, under
    /// <c>Spark:Replication:Sources:{TargetCollection}</c>. In an assembly marked
    /// <see cref="SparkReplicaContractsAttribute"/>, a class the host does not bind is inert instead of refusing startup.
    /// </summary>
    public string? SourceModule { get; init; }

    /// <summary>
    /// The consumer's collection the replicas are stored in. Defaults to the generator's pluralisation of
    /// the class name (<c>Car</c> → <c>Cars</c>, <c>Person</c> → <c>People</c>); startup verifies it equals
    /// the collection the consumer's document store assigns to the type, so set it whenever the store's
    /// conventions differ.
    /// </summary>
    public string? TargetCollection { get; init; }

    /// <summary>
    /// The owner's CLR type, when the consumer can reference the owner's library. The generator then checks
    /// every mapped path against it (SPARK067). Nothing at runtime reads it.
    /// </summary>
    public Type? OriginalType { get; init; }

    /// <summary>
    /// Whether edits of this replica go back to the owner as SyncActions. Off by default: the type is then
    /// fully read-only.
    /// </summary>
    public bool WriteBack { get; init; }
}
