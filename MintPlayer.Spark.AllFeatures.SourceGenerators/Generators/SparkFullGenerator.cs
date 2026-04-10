using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MintPlayer.Spark.AllFeatures.SourceGenerators.Models;
using MintPlayer.SourceGenerators.Tools;
using MintPlayer.SourceGenerators.Tools.ValueComparers;

namespace MintPlayer.Spark.AllFeatures.SourceGenerators.Generators;

[Generator(LanguageNames.CSharp)]
public class SparkFullGenerator : IncrementalGenerator
{
    public override void Initialize(
        IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<Settings> settingsProvider,
        IncrementalValueProvider<ICompilationCache> cacheProvider)
    {
        // Discover SparkContext subclasses, SparkUser subclasses, and existence of
        // Actions / CustomAction / Recipient classes in user source code.
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

                        if (baseType.IsGenericType &&
                            baseType.ConstructedFrom.ToDisplayString() == "MintPlayer.Spark.Actions.DefaultPersistentObjectActions<T>")
                        {
                            return new SparkFullDiscoveredType
                            {
                                Kind = "Actions",
                                TypeName = string.Empty
                            };
                        }

                        baseType = baseType.BaseType;
                    }

                    // Check interfaces for ICustomAction and IRecipient<T>
                    foreach (var iface in classSymbol.AllInterfaces)
                    {
                        if (iface.ToDisplayString() == "MintPlayer.Spark.Abstractions.Actions.ICustomAction")
                        {
                            return new SparkFullDiscoveredType
                            {
                                Kind = "CustomAction",
                                TypeName = string.Empty
                            };
                        }

                        if (iface.IsGenericType &&
                            iface.ConstructedFrom.ToDisplayString() == "MintPlayer.Spark.Messaging.Abstractions.IRecipient<TMessage>")
                        {
                            return new SparkFullDiscoveredType
                            {
                                Kind = "Recipient",
                                TypeName = string.Empty
                            };
                        }
                    }

                    return default;
                })
            .Where(static x => x != null)
            .WithNullableComparer()
            .Collect();

        // Check which Spark packages are referenced (compilation-level checks)
        var featureFlagsProvider = context.CompilationProvider
            .Select(static (compilation, ct) => new SparkFullFeatureFlags
            {
                HasSpark = compilation.GetTypeByMetadataName("MintPlayer.Spark.SparkContext") != null,
                HasSparkUser = compilation.GetTypeByMetadataName("MintPlayer.Spark.Authorization.Identity.SparkUser") != null,
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
