using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using MintPlayer.Spark.Layering;
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

        // The run time's own engine (composition D14), so the two can never disagree on a conflict.
        SparkComposition composition;
        try
        {
            composition = SparkLayers.Compose(LibraryActionsReader.Parse(layers), SparkKinds.Actions);
        }
        catch (SparkLayerException)
        {
            // The run time refuses a layer that does not compose, in its own words.
            return;
        }

        foreach (var conflict in composition.Conflicts)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                ConflictRule, Location.None, conflict.Path[0], conflict.Path[1], conflict.WinnerLayer, conflict.LoserLayer));
        }
    }
}
