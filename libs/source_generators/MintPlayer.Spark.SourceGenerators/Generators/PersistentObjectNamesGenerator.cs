using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MintPlayer.Spark.SourceGenerators.Json;
using MintPlayer.Spark.SourceGenerators.Models;
using MintPlayer.SourceGenerators.Tools;
using System.IO;

namespace MintPlayer.Spark.SourceGenerators.Generators;

[Generator(LanguageNames.CSharp)]
public class PersistentObjectNamesGenerator : IncrementalGenerator
{
    public override void Initialize(
        IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<Settings> settingsProvider)
    {
        // Find all classes that inherit from DefaultPersistentObjectActions<T> and extract the
        // entity type + its public read/write instance properties (minus "Id").
        var persistentObjectsProvider = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, ct) => node is ClassDeclarationSyntax classDecl &&
                    classDecl.BaseList != null &&
                    classDecl.BaseList.Types.Count > 0,
                transform: static (ctx, ct) =>
                {
                    if (ctx.Node is not ClassDeclarationSyntax classDeclaration)
                        return default;

                    var classSymbol = ctx.SemanticModel.GetDeclaredSymbol(classDeclaration, ct) as INamedTypeSymbol;
                    if (classSymbol == null || classSymbol.IsAbstract)
                        return default;

                    var baseType = classSymbol.BaseType;
                    while (baseType != null)
                    {
                        if (baseType.IsGenericType &&
                            baseType.ConstructedFrom.ToDisplayString() == "MintPlayer.Spark.Actions.DefaultPersistentObjectActions<T>")
                        {
                            if (baseType.TypeArguments.FirstOrDefault() is not INamedTypeSymbol entityType)
                                return default;

                            var attributeNames = new List<string>();
                            var seen = new HashSet<string>(StringComparer.Ordinal);
                            var current = entityType;
                            while (current != null && current.SpecialType != SpecialType.System_Object)
                            {
                                foreach (var member in current.GetMembers())
                                {
                                    if (member is not IPropertySymbol property)
                                        continue;
                                    if (property.IsStatic || property.IsIndexer)
                                        continue;
                                    if (property.DeclaredAccessibility != Accessibility.Public)
                                        continue;
                                    if (property.GetMethod == null || property.SetMethod == null)
                                        continue;
                                    if (property.Name == "Id")
                                        continue;
                                    // Excluded from the model, so there is no attribute for a
                                    // constant to name.
                                    if (property.IsIgnoredForSparkModel())
                                        continue;
                                    if (!seen.Add(property.Name))
                                        continue;

                                    attributeNames.Add(property.Name);
                                }
                                current = current.BaseType;
                            }

                            return new PersistentObjectInfo
                            {
                                EntityName = entityType.Name,
                                EntityFullName = entityType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                                AttributeNames = attributeNames,
                            };
                        }
                        baseType = baseType.BaseType;
                    }

                    return default;
                })
            .Where(static x => x != null)
            .Collect();

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

        var combinedProvider = persistentObjectsProvider
            .Combine(knowsSparkProvider)
            .Combine(settingsProvider);

        var namesProvider = combinedProvider
            .Combine(libraryModelFilesProvider)
            .Select(static (p, ct) =>
            {
                var providers = p.Left;
                var persistentObjects = providers.Left.Left;
                var knowsSpark = providers.Left.Right;
                var settings = providers.Right;
                var libraryTypes = ComposedModel.LibraryTypeNames(p.Right)
                    .Select(name => new PersistentObjectInfo { EntityName = name, EntityFullName = name });

                return (Producer)new PersistentObjectNamesProducer(
                    persistentObjects.Where(x => x != null).Cast<PersistentObjectInfo>().Concat(libraryTypes),
                    knowsSpark,
                    settings.RootNamespace ?? "GeneratedCode");
            });

        var attributeNamesProvider = combinedProvider
            .Select(static (providers, ct) =>
            {
                var persistentObjects = providers.Left.Left;
                var knowsSpark = providers.Left.Right;
                var settings = providers.Right;

                return (Producer)new AttributeNamesProducer(
                    persistentObjects.Where(x => x != null).Cast<PersistentObjectInfo>(),
                    knowsSpark,
                    settings.RootNamespace ?? "GeneratedCode");
            });

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

        var idsSourceProvider = idsProvider
            .Combine(knowsSparkProvider)
            .Combine(settingsProvider)
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

        context.ProduceCode(namesProvider, attributeNamesProvider, idsSourceProvider);
    }
}
