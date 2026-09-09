using Microsoft.CodeAnalysis;

namespace MintPlayer.Spark.LibraryGenerators.Generators;

public partial class ValueObjectKeyReporter
{
    /// <summary>
    /// Error, not warning, and reported rather than silently skipped.
    /// </summary>
    /// <remarks>
    /// The key is what lets a save tell an added row from a removed one, which is what the row-level
    /// <c>New</c> and <c>Delete</c> rights are decided from. A type that quietly went unkeyed would
    /// leave its collection unjudgeable — the one failure this whole mechanism exists to make
    /// impossible — and the symptom would surface far away, as a save refused or permitted for
    /// reasons nothing in the file explains.
    /// <para>
    /// The fix is a single keyword, and the compiler already demands it for any generated member, so
    /// an error costs the author nothing they were not about to be told anyway. An author who
    /// already has a key says so with <c>[ValueKey]</c> instead, and is not asked for <c>partial</c>
    /// at all.
    /// </para>
    /// </remarks>
    internal static readonly DiagnosticDescriptor ValueObjectMustBePartialRule = new(
        id: "SPARK016",
        title: "Value object must be partial",
        messageFormat: "'{0}' is decorated [ValueObject], so it needs a generated row key — declare it 'partial', or mark an existing property [ValueKey].",
        category: "Correctness",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A type decorated [ValueObject] is stored embedded in the document that owns it and is given a generated Id so the framework can match rows across saves. Without a stable key a save cannot tell an added row from a removed one, and the row-level New and Delete rights derived from that comparison cannot be enforced. Declaring the type partial lets the generator supply the key; a type that already has a suitable key marks it [ValueKey] and keeps it.");
}
