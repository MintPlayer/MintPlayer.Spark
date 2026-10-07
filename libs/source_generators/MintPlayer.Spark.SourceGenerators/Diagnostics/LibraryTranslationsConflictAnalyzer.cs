using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using MintPlayer.Spark.Layering;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

/// <summary>
/// SPARK_TRANS_005: two referenced libraries translate the same key into the same language
/// differently (#467, D3). Composition M5 (D10) moved it here from the host translations aggregator,
/// which is gone: the run time composes translations itself.
/// </summary>
/// <remarks>
/// The libraries' <c>[assembly: SparkLayer]</c> translations layers and the application's own
/// <c>translations.json</c> (an AdditionalFile) are composed by the run time's engine
/// (<see cref="SparkTranslationLayers"/>), in dependency order, so the two can never disagree. Only an
/// application is analyzed, as the aggregator was. Overriding a library is what the application's file
/// is for, and a library overriding a library it depends on means it (grill Q3); neither is reported.
/// The same conflict is logged at startup.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LibraryTranslationsConflictAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [TranslationsDiagnostics.ConflictingKey];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationAction(Analyze);
    }

    private static void Analyze(CompilationAnalysisContext context)
    {
        if (context.Compilation.Options.OutputKind is not (OutputKind.ConsoleApplication or OutputKind.WindowsApplication))
            return;

        var libraries = LibraryLayersReader.Read(context.Compilation)
            .Where(l => l.Files.Any(f => f.Kind == SparkLayerKinds.Translations))
            .ToList();
        if (libraries.Count < 2) return;

        var inputs = new List<SparkTranslationsInput>();
        foreach (var library in libraries)
            foreach (var file in library.Files.Where(f => f.Kind == SparkLayerKinds.Translations))
                inputs.Add(new SparkTranslationsInput(library.Assembly, file.Json, isLibrary: true, library.DependsOn));

        SparkTranslationsResult result;
        try
        {
            result = SparkTranslationLayers.Compose(inputs.Concat(Application(context)));
        }
        catch (SparkLayerException)
        {
            // The run time refuses a layer that does not compose, in its own words; SPARK_TRANS_001-004
            // report the application's file.
            return;
        }

        foreach (var conflict in result.Conflicts)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                TranslationsDiagnostics.ConflictingKey, Location.None,
                conflict.Key, conflict.Language, conflict.WinnerLayer, conflict.LoserLayer));
        }
    }

    /// <summary>The application's own <c>translations.json</c>, when it has one that reads; the library layers compose without it otherwise.</summary>
    private static IEnumerable<SparkTranslationsInput> Application(CompilationAnalysisContext context)
    {
        foreach (var text in context.Options.AdditionalFiles)
        {
            if (!string.Equals(Path.GetFileName(text.Path), "translations.json", System.StringComparison.OrdinalIgnoreCase)) continue;
            if (text.GetText(context.CancellationToken)?.ToString() is not { Length: > 0 } json) continue;

            try
            {
                SparkTranslationLayers.Flatten("translations.json", json);
            }
            catch (SparkLayerException)
            {
                continue;
            }
            yield return new SparkTranslationsInput("translations.json", json, isLibrary: false);
        }
    }
}
