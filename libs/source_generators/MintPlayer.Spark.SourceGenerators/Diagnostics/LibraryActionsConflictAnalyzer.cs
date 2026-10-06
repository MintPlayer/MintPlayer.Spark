using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using MintPlayer.Spark.Layering;
using System.Collections.Immutable;
using System.Linq;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

/// <summary>
/// SPARK036: two referenced libraries state the same property of the same action differently
/// (#467, D7 — mirrors SPARK_TRANS_005 for translations).
/// </summary>
/// <remarks>
/// The libraries' <c>actions.json</c> layers compose in dependency order, by assembly name between
/// unrelated ones (<c>SparkLibraryOrder</c>), so the later one silently
/// wins. The application's own file is never part of a conflict: overriding a library is what it
/// is for. The same conflict is logged at startup by the run-time composition.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LibraryActionsConflictAnalyzer : DiagnosticAnalyzer
{
    internal static readonly DiagnosticDescriptor ConflictRule = new(
        id: "SPARK036",
        title: "Two libraries define the same action differently",
        messageFormat: "Libraries '{2}' and '{3}' both state '{1}' of the action '{0}', with different values. '{2}' wins (libraries apply in dependency order, by assembly name between unrelated ones). State '{1}' of '{0}' in the app's actions.json to choose.",
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
        var libraries = LibraryLayersReader.Read(context.Compilation)
            .Where(l => l.Files.Any(f => f.Kind == SparkLayerKinds.Actions))
            .ToList();
        if (libraries.Count < 2) return;

        // The run time's own engine (composition D14), so the two can never disagree on a conflict.
        SparkComposition composition;
        try
        {
            composition = SparkLayers.Compose(LibraryLayersReader.Parse(libraries, SparkLayerKinds.Actions), SparkKinds.Actions);
        }
        catch (SparkLayerException)
        {
            // The run time refuses a layer that does not compose, in its own words.
            return;
        }

        // A library overriding a library it depends on means it (grill Q3); only unrelated ones conflict.
        var dependsOn = libraries.ToDictionary(l => l.Assembly, l => l.DependsOn, System.StringComparer.Ordinal);
        foreach (var conflict in composition.Conflicts)
        {
            if (dependsOn.TryGetValue(conflict.WinnerLayer, out var below) && below.Contains(conflict.LoserLayer))
                continue;

            context.ReportDiagnostic(Diagnostic.Create(
                ConflictRule, Location.None, conflict.Path[0], conflict.Path[1], conflict.WinnerLayer, conflict.LoserLayer));
        }
    }
}
