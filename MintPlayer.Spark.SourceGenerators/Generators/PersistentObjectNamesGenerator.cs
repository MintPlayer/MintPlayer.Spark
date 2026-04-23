using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MintPlayer.Spark.SourceGenerators.Models;
using MintPlayer.SourceGenerators.Tools;
using MintPlayer.SourceGenerators.Tools.ValueComparers;

namespace MintPlayer.Spark.SourceGenerators.Generators;

[Generator(LanguageNames.CSharp)]
public class PersistentObjectNamesGenerator : IncrementalGenerator
{
    public override void Initialize(
        IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<Settings> settingsProvider,
        IncrementalValueProvider<ICompilationCache> cacheProvider)
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
            .WithNullableComparer()
            .Collect();

        // Only emit when the project actually references MintPlayer.Spark.
        var knowsSparkProvider = context.CompilationProvider
            .Select((compilation, ct) =>
                compilation.GetTypeByMetadataName("MintPlayer.Spark.Actions.DefaultPersistentObjectActions`1") != null);

        var combinedProvider = persistentObjectsProvider
            .Combine(knowsSparkProvider)
            .Combine(settingsProvider);

        var namesProvider = combinedProvider
            .Select(static (providers, ct) =>
            {
                var persistentObjects = providers.Left.Left;
                var knowsSpark = providers.Left.Right;
                var settings = providers.Right;

                return (Producer)new PersistentObjectNamesProducer(
                    persistentObjects.Where(x => x != null).Cast<PersistentObjectInfo>(),
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

        context.ProduceCode(namesProvider, attributeNamesProvider);
    }
}
