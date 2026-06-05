using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MintPlayer.Spark.SourceGenerators.Models;
using MintPlayer.SourceGenerators.Tools;
using MintPlayer.SourceGenerators.Tools.ValueComparers;

namespace MintPlayer.Spark.SourceGenerators.Generators;

[Generator(LanguageNames.CSharp)]
public class CronJobRegistrationGenerator : IncrementalGenerator
{
    public override void Initialize(
        IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<Settings> settingsProvider,
        IncrementalValueProvider<ICompilationCache> cacheProvider)
    {
        // Find all non-abstract classes that implement MintPlayer.Spark.Cron.ISparkCronJob
        var jobClassesProvider = context.SyntaxProvider
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

                    foreach (var iface in classSymbol.AllInterfaces)
                    {
                        if (iface.ToDisplayString() == "MintPlayer.Spark.Cron.ISparkCronJob")
                        {
                            return new CronJobClassInfo
                            {
                                JobTypeName = classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                            };
                        }
                    }

                    return default;
                })
            .Where(static x => x != null)
            .WithNullableComparer()
            .Collect();

        // Only emit when the project actually references MintPlayer.Spark.Cron
        var knowsCronProvider = context.CompilationProvider
            .Select((compilation, ct) =>
                compilation.GetTypeByMetadataName("MintPlayer.Spark.Cron.ISparkCronJob") != null);

        var sourceProvider = jobClassesProvider
            .Combine(knowsCronProvider)
            .Combine(settingsProvider)
            .Select(static (providers, ct) =>
            {
                var jobClasses = providers.Left.Left;
                var knowsCron = providers.Left.Right;
                var settings = providers.Right;

                return (Producer)new CronJobRegistrationProducer(
                    jobClasses.Where(x => x != null).Cast<CronJobClassInfo>(),
                    knowsCron,
                    settings.RootNamespace ?? "GeneratedCode");
            });

        context.ProduceCode(sourceProvider);
    }
}
