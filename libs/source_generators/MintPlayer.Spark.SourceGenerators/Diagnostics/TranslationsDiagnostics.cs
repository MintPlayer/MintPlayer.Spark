using Microsoft.CodeAnalysis;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

internal static class TranslationsDiagnostics
{
    private const string Category = "SparkTranslations";

    public static readonly DiagnosticDescriptor InvalidJson = new(
        id: "SPARK_TRANS_001",
        title: "Invalid translations.json",
        messageFormat: "translations.json is not valid JSON: {0}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MixedLeafAndNamespace = new(
        id: "SPARK_TRANS_002",
        title: "Mixed leaf/namespace object in translations.json",
        messageFormat: "Object at '{0}' mixes string and object values. A translation leaf must have only string values; a namespace must have only object values.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor EmptyObject = new(
        id: "SPARK_TRANS_003",
        title: "Empty object in translations.json",
        messageFormat: "Object at '{0}' is empty and contributes no translations; it has been skipped.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ArrayNotAllowed = new(
        id: "SPARK_TRANS_004",
        title: "Array not allowed in translations.json",
        messageFormat: "Value at '{0}' is an array. Arrays are not allowed in translations.json.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ConflictingKey = new(
        id: "SPARK_TRANS_005",
        title: "Two libraries translate the same key differently",
        messageFormat: "Libraries '{2}' and '{3}' both translate '{0}' into '{1}', with different values. '{2}' wins (libraries apply alphabetically). Define '{0}' in the app's translations.json to choose.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);
}
