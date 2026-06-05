using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MintPlayer.Spark.SourceGenerators.Models;
using MintPlayer.SourceGenerators.Tools;
using MintPlayer.SourceGenerators.Tools.ValueComparers;

namespace MintPlayer.Spark.SourceGenerators.Generators;

[Generator(LanguageNames.CSharp)]
public class SubscriptionWorkerRegistrationGenerator : IncrementalGenerator
{
    public override void Initialize(
        IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<Settings> settingsProvider,
        IncrementalValueProvider<ICompilationCache> cacheProvider)
    {
        // Find all classes that inherit from SparkSubscriptionWorker<T>
        var workerClassesProvider = context.SyntaxProvider
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

                    // Check if this class inherits from SparkSubscriptionWorker<T>
                    var baseType = classSymbol.BaseType;
                    while (baseType != null)
                    {
                        if (baseType.IsGenericType &&
                            baseType.ConstructedFrom.ToDisplayString() == "MintPlayer.Spark.SubscriptionWorker.SparkSubscriptionWorker<T>")
                        {
                            return new SubscriptionWorkerClassInfo
                            {
                                WorkerTypeName = classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                            };
                        }
                        baseType = baseType.BaseType;
                    }

                    return default;
                })
            .Where(static x => x != null)
            .WithNullableComparer()
            .Collect();

        // Check if project references MintPlayer.Spark.SubscriptionWorker
        var knowsSubscriptionWorkerProvider = context.CompilationProvider
            .Select((compilation, ct) =>
                compilation.GetTypeByMetadataName("MintPlayer.Spark.SubscriptionWorker.SparkSubscriptionWorker`1") != null);

        // Combine and produce the source
        var sourceProvider = workerClassesProvider
            .Combine(knowsSubscriptionWorkerProvider)
            .Combine(settingsProvider)
            .Select(static (providers, ct) =>
            {
                var workerClasses = providers.Left.Left;
                var knowsSubscriptionWorker = providers.Left.Right;
                var settings = providers.Right;

                return (Producer)new SubscriptionWorkerRegistrationProducer(
                    workerClasses.Where(x => x != null).Cast<SubscriptionWorkerClassInfo>(),
                    knowsSubscriptionWorker,
                    settings.RootNamespace ?? "GeneratedCode");
            });

        context.ProduceCode(sourceProvider);
    }
}
