using Microsoft.CodeAnalysis;

namespace MintPlayer.Spark.LibraryGenerators.Generators;

/// <remarks>
/// All four are errors, not a runtime fallback: without the generated constants the runtime has no script
/// for the type (warehouse WD14), and a replica that silently never replicates surfaces far away, as empty
/// data in another module.
/// </remarks>
public partial class ReplicatedReporter
{
    internal const string MustBePartialId = "SPARK065";
    internal const string InvalidDeclarationId = "SPARK066";
    internal const string MissingPathId = "SPARK067";
    internal const string CollidingMemberId = "SPARK068";

    /// <summary>
    /// The generator reopens the class (and every class containing it) to add the constants. A generic
    /// class reuses the id: it cannot be one concrete document shape, and <c>typeof</c> in the assembly
    /// list would name an open type.
    /// </summary>
    internal static readonly DiagnosticDescriptor MustBePartialRule = new(
        id: MustBePartialId,
        title: "[Replicated] class must be partial and not generic",
        messageFormat: "'{0}' is decorated [Replicated], so its replication script is generated into it, but {1}",
        category: "Correctness",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The replication manifest, ETL script and hash of a [Replicated] class are generated into the class as constants. The class and every class containing it must be declared partial, and none of them may be generic.");

    /// <summary>
    /// Every name reaches the generated JavaScript, so each is held to <c>^[A-Za-z_][A-Za-z0-9_]*$</c>
    /// (F16): the script can then only ever be member accesses and one <c>loadTo</c> call.
    /// </summary>
    internal static readonly DiagnosticDescriptor InvalidDeclarationRule = new(
        id: InvalidDeclarationId,
        title: "[Replicated] declaration cannot be rendered",
        messageFormat: "'{0}' cannot be replicated: {1}",
        category: "Correctness",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "SourceCollection is required, and the source and target collections, every mapped property and every segment of a [ReplicatedFrom] path must be plain identifiers (^[A-Za-z_][A-Za-z0-9_]*$). A [Replicated] class must map at least one property.");

    internal static readonly DiagnosticDescriptor MissingPathRule = new(
        id: MissingPathId,
        title: "[Replicated] source path does not exist on OriginalType",
        messageFormat: "'{0}' reads '{1}' into '{2}', but '{3}' has no such property path",
        category: "Correctness",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "When [Replicated] names an OriginalType, every mapped source path is checked against it: each segment must be a property of the previous segment's type (inherited properties count, Nullable<T> is unwrapped).");

    /// <summary>
    /// There is no hand-written script override: a consumer-authored script is exactly F16, and it could not
    /// run on the HTTP transport. A derived value is computed by the consumer from the replica.
    /// </summary>
    internal static readonly DiagnosticDescriptor CollidingMemberRule = new(
        id: CollidingMemberId,
        title: "[Replicated] class declares a generated member",
        messageFormat: "'{0}' declares '{1}', which the [Replicated] generator emits; remove or rename it (the replication script is always generated)",
        category: "Correctness",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The [Replicated] generator emits ReplicationSourceCollection, ReplicationTargetCollection, ReplicationSourceModule, ReplicationWriteBack, ReplicationManifest, ReplicationScript and ReplicationHash. A hand-written member of one of these names is refused: there is no hand-written script override.");
}
