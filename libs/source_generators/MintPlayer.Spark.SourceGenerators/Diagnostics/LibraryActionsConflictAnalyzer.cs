using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

/// <summary>
/// SPARK036: two referenced libraries state the same property of the same action differently
/// (#467, D7 — mirrors SPARK_TRANS_005 for translations).
/// </summary>
/// <remarks>
/// The libraries' <c>actions.json</c> layers compose by assembly name, so the later one silently
/// wins. The application's own file is never part of a conflict: overriding a library is what it
/// is for. The same conflict is logged at startup by the run-time composition.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LibraryActionsConflictAnalyzer : DiagnosticAnalyzer
{
    internal static readonly DiagnosticDescriptor ConflictRule = new(
        id: "SPARK036",
        title: "Two libraries define the same action differently",
        messageFormat: "Libraries '{2}' and '{3}' both state '{1}' of the action '{0}', with different values. '{2}' wins (libraries apply by assembly name). State '{1}' of '{0}' in the app's actions.json to choose.",
        category: "Spark",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [ConflictRule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationAction(Analyze);
    }

    private static void Analyze(CompilationAnalysisContext context)
    {
        var layers = LibraryActionsReader.Read(context.Compilation);
        if (layers.Count < 2) return;

        // action → property → (canonical value, layer)
        var composed = new Dictionary<string, Dictionary<string, (string Value, string Layer)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in layers)
        {
            foreach (var entry in LibraryActionsReader.Entries(layer.Json))
            {
                if (entry.Value is null)
                {
                    composed.Remove(entry.Key);
                    continue;
                }

                if (!composed.TryGetValue(entry.Key, out var properties))
                    composed[entry.Key] = properties = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);

                foreach (var property in entry.Value)
                {
                    if (property.Value == "null")
                    {
                        properties.Remove(property.Key);
                        continue;
                    }

                    if (properties.TryGetValue(property.Key, out var earlier)
                        && !string.Equals(earlier.Layer, layer.Assembly, StringComparison.OrdinalIgnoreCase)
                        && earlier.Value != property.Value)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            ConflictRule, Location.None, entry.Key, property.Key, layer.Assembly, earlier.Layer));
                    }

                    properties[property.Key] = (property.Value, layer.Assembly);
                }
            }
        }
    }
}
