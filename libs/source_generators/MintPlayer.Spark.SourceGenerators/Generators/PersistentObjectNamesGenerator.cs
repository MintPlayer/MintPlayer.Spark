using Microsoft.CodeAnalysis;
using MintPlayer.Spark.SourceGenerators.Json;
using MintPlayer.Spark.SourceGenerators.Models;
using MintPlayer.SourceGenerators.Tools;
using System.IO;

namespace MintPlayer.Spark.SourceGenerators.Generators;

/// <summary>
/// Emits <c>PersistentObjectNames</c> and <c>PersistentObjectIds</c> for every persistent object of the
/// composed model (composition D6): the referenced libraries' model layers, then the application's own
/// <c>&lt;SparkAppDataDir&gt;/Model/*.json</c> on top.
/// </summary>
/// <remarks>
/// Both come from the one composition, so a name and its id always agree, a library type has both in the
/// application without a copy of its own, and a CLR-less type (a virtual <c>Home</c>) has both too. The
/// CLR class names are never consulted: they are not the model's names (X20 item 1, D28).
/// <para>
/// The <c>AttributeNames</c> constants this generator used to emit are gone (X20 item 1): nothing used
/// them, and <c>nameof(Entity.Property)</c> already names an attribute with compile-time checking.
/// </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public class PersistentObjectNamesGenerator : IncrementalGenerator
{
    public override void Initialize(
        IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<Settings> settingsProvider)
    {
        // Only emit when the project actually references MintPlayer.Spark.
        var knowsSparkProvider = context.CompilationProvider
            .Select((compilation, ct) =>
                compilation.GetTypeByMetadataName("MintPlayer.Spark.Actions.DefaultPersistentObjectActions`1") != null);

        // The model files the referenced libraries ship (composition D6): their types have names and ids
        // in the application although the application has no copy of them. Only an application composes
        // the model; a library referencing another gets no constants for types it does not own.
        var libraryModelFilesProvider = context.CompilationProvider
            .Select(static (compilation, _) => compilation.Options.OutputKind is OutputKind.ConsoleApplication or OutputKind.WindowsApplication
                ? ComposedModel.LibraryFiles(compilation)
                : new List<LibraryModelFileInfo>())
            .WithComparer(SequenceComparer<LibraryModelFileInfo>.Instance);

        // Harvest PersistentObjectIdInfo from the composed model (composition D6): the referenced
        // libraries' model layers, then <SparkAppDataDir>/Model/*.json AdditionalFiles on top, so a
        // library type has its id here without a copy in the app. Files that don't parse as Spark
        // Model JSON (missing "persistentObject" wrapper, missing id/name, bad Guid) are silently
        // skipped — they may be other auxiliary JSON files the host includes. A library's own layer
        // files (SparkLayerPath metadata) are not the application's model: they travel in its dll.
        var appDataDirProvider = context.AnalyzerConfigOptionsProvider
            .Select(static (options, _) => SparkAppDataDir.Read(options.GlobalOptions));

        var appModelFilesProvider = context.AdditionalTextsProvider
            .Combine(appDataDirProvider)
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Where(static p =>
            {
                var file = Path.GetFileName(p.Left.Left.Path);
                if (!file.EndsWith(".json", System.StringComparison.OrdinalIgnoreCase))
                    return false;
                if (p.Right.GetOptions(p.Left.Left).TryGetValue(LibraryLayersGenerator.PathMetadata, out var layerPath)
                    && !string.IsNullOrWhiteSpace(layerPath))
                    return false;
                // Path convention: <anything>/<SparkAppDataDir>/Model/<EntityName>.json
                return SparkAppDataDir.Contains(p.Left.Left.Path, p.Left.Right, "Model");
            })
            .Select(static (p, ct) => new AppModelFileInfo
            {
                Path = p.Left.Left.Path,
                Text = p.Left.Left.GetText(ct)?.ToString() ?? string.Empty,
            })
            .Collect();

        var idsProvider = appModelFilesProvider
            .Combine(libraryModelFilesProvider)
            .Select(static (p, ct) =>
            {
                var ids = new List<PersistentObjectIdInfo>();
                foreach (var json in ComposedModel.Compose(p.Right, p.Left))
                {
                    if (ModelJsonReader.TryRead(json, out var info) && info is not null)
                        ids.Add(info);
                }
                return ids;
            });

        var composedProvider = idsProvider
            .Combine(knowsSparkProvider)
            .Combine(settingsProvider);

        var namesSourceProvider = composedProvider
            .Select(static (providers, ct) =>
            {
                var ids = providers.Left.Left;
                var knowsSpark = providers.Left.Right;
                var settings = providers.Right;

                return (Producer)new PersistentObjectNamesProducer(
                    ids.Select(i => i.Name),
                    knowsSpark,
                    settings.RootNamespace ?? "GeneratedCode");
            });

        var idsSourceProvider = composedProvider
            .Select(static (providers, ct) =>
            {
                var ids = providers.Left.Left;
                var knowsSpark = providers.Left.Right;
                var settings = providers.Right;

                return (Producer)new PersistentObjectIdsProducer(
                    ids,
                    knowsSpark,
                    settings.RootNamespace ?? "GeneratedCode");
            });

        context.ProduceCode(namesSourceProvider, idsSourceProvider);
    }
}
