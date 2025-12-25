using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MintPlayer.Spark.SourceGenerators.Models;
using MintPlayer.SourceGenerators.Tools;
using MintPlayer.SourceGenerators.Tools.ValueComparers;

namespace MintPlayer.Spark.SourceGenerators.Generators;

[Generator(LanguageNames.CSharp)]
public class ActionsRegistrationGenerator : IncrementalGenerator
{
    public override void Initialize(
        IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<Settings> settingsProvider,
        IncrementalValueProvider<ICompilationCache> cacheProvider)
    {
        // Find all classes that inherit from DefaultPersistentObjectActions<T>
        var actionsClassesProvider = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, ct) => node is ClassDeclarationSyntax classDecl &&
                    classDecl.BaseList != null &&
                    classDecl.BaseList.Types.Count > 0,
                transform: static (ctx, ct) =>
                {
                    if (ctx.Node is not ClassDeclarationSyntax classDeclaration)
                        return default;

                    var semanticModel = ctx.SemanticModel;
                    var classSymbol = semanticModel.GetDeclaredSymbol(classDeclaration, ct) as INamedTypeSymbol;
                    if (classSymbol == null || classSymbol.IsAbstract)
                        return default;

                    // Check if this class inherits from DefaultPersistentObjectActions<T>
                    var baseType = classSymbol.BaseType;
                    while (baseType != null)
                    {
                        if (baseType.IsGenericType &&
                            baseType.ConstructedFrom.ToDisplayString() == "MintPlayer.Spark.Actions.DefaultPersistentObjectActions<T>")
                        {
                            // Extract the entity type from the generic parameter
                            var entityType = baseType.TypeArguments.FirstOrDefault();
                            if (entityType != null)
                            {
                                return new ActionsClassInfo
                                {
                                    ActionsTypeName = classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                                    EntityTypeName = entityType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                                };
                            }
                        }
                        baseType = baseType.BaseType;
                    }

                    return default;
                })
            .Where(static x => x != null)
            .Collect();

        // Check if project references MintPlayer.Spark
        var knowsSparkProvider = context.CompilationProvider
            .Select((compilation, ct) =>
                compilation.GetTypeByMetadataName("MintPlayer.Spark.Actions.DefaultPersistentObjectActions`1") != null);

        // Combine and produce the source
        var sourceProvider = actionsClassesProvider
            .Combine(knowsSparkProvider)
            .Combine(settingsProvider)
            .Select(static (providers, ct) =>
            {
                var actionsClasses = providers.Left.Left;
                var knowsSpark = providers.Left.Right;
                var settings = providers.Right;

                return (Producer)new ActionsRegistrationProducer(
                    actionsClasses.Where(x => x != null).Cast<ActionsClassInfo>(),
                    knowsSpark,
                    settings.RootNamespace ?? "GeneratedCode");
            });

        context.ProduceCode(sourceProvider);
    }
}
