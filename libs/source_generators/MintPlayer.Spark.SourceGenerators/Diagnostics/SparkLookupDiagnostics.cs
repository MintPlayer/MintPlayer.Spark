using Microsoft.CodeAnalysis;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

/// <summary>Diagnostics reported by <c>SparkLookupGenerator</c> (commerce X20 item 5).</summary>
internal static class SparkLookupDiagnostics
{
    private const string Category = "SparkLookup";

    /// <summary>
    /// A <c>[SparkLookup]</c> enum the generator cannot emit a class for. Reported rather than skipped:
    /// a missing class is a lookup that model files name and the server never serves.
    /// </summary>
    public static readonly DiagnosticDescriptor InvalidLookup = new(
        id: "SPARK064",
        title: "[SparkLookup] enum cannot be emitted",
        messageFormat: "'{0}' is marked [SparkLookup] but {1}. No lookup class was emitted for it.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
