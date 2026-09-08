using Microsoft.CodeAnalysis;

namespace MintPlayer.Spark.SourceGenerators.Generators;

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
    /// an error costs the author nothing they were not about to be told anyway.
    /// </para>
    /// </remarks>
    internal static readonly DiagnosticDescriptor ValueObjectMustBePartialRule = new(
        id: "SPARK015",
        title: "Embedded collection type must be partial",
        messageFormat: "'{0}' is the element of an embedded collection, so it needs a generated row key — declare it 'partial'.",
        category: "Correctness",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A type used as the element of an embedded collection is given a generated Id so the framework can match rows across saves. Without a stable key a save cannot tell an added row from a removed one, and the row-level New and Delete rights derived from that comparison cannot be enforced. Declaring the type partial lets the generator supply the key; a type that already declares its own Id keeps it and is left alone.");
}
