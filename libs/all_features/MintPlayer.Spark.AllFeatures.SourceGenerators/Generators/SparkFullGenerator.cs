using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MintPlayer.Spark.AllFeatures.SourceGenerators.Models;
using MintPlayer.SourceGenerators.Tools;

namespace MintPlayer.Spark.AllFeatures.SourceGenerators.Generators;

[Generator(LanguageNames.CSharp)]
public class SparkFullGenerator : IncrementalGenerator
{
    public override void Initialize(
        IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<Settings> settingsProvider)
    {
        // Discover the SparkContext and SparkUser subclasses. Everything else the bundle registers
        // (Actions, custom actions, recipients, cron jobs, migrations, interceptors, …) is found by
        // MintPlayer.Spark.SourceGenerators' SparkDiscoveryGenerator, whose AddSparkDiscovered()
        // AddSparkFull calls: one scan, not two (X20 item 3).
        var discoveryProvider = context.SyntaxProvider
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

                    // Walk base type chain
                    var baseType = classSymbol.BaseType;
                    while (baseType != null)
                    {
                        if (baseType.ToDisplayString() == "MintPlayer.Spark.SparkContext")
                        {
                            return new SparkFullDiscoveredType
                            {
                                Kind = "Context",
                                TypeName = classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                            };
                        }

                        if (baseType.ToDisplayString() == "MintPlayer.Spark.Authorization.Identity.SparkUser")
                        {
                            return new SparkFullDiscoveredType
                            {
                                Kind = "User",
                                TypeName = classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                            };
                        }

                        baseType = baseType.BaseType;
                    }

                    return default;
                })
            .Where(static x => x != null)
            .Collect();

        // Check which Spark packages are referenced (compilation-level checks)
        var featureFlagsProvider = context.CompilationProvider
            .Select(static (compilation, ct) => new SparkFullFeatureFlags
            {
                HasSpark = compilation.GetTypeByMetadataName("MintPlayer.Spark.SparkContext") != null,
                HasAuthorization = compilation.GetTypeByMetadataName("MintPlayer.Spark.Authorization.Extensions.SparkBuilderAuthorizationExtensions") != null,
                HasMessaging = compilation.GetTypeByMetadataName("MintPlayer.Spark.Messaging.SparkBuilderMessagingExtensions") != null,
                HasReplication = compilation.GetTypeByMetadataName("MintPlayer.Spark.Replication.SparkBuilderReplicationExtensions") != null,
            });

        // Combine all providers and produce source
        var sourceProvider = discoveryProvider
            .Combine(featureFlagsProvider)
            .Combine(settingsProvider)
            .Select(static (providers, ct) =>
            {
                var discoveries = providers.Left.Left;
                var flags = providers.Left.Right;
                var settings = providers.Right;

                return (Producer)new SparkFullProducer(
                    discoveries.Where(x => x != null).Cast<SparkFullDiscoveredType>(),
                    flags,
                    settings.RootNamespace ?? "GeneratedCode");
            });

        context.ProduceCode(sourceProvider);
    }
}
