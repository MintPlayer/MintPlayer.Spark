using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MintPlayer.Spark.SourceGenerators.Models;
using MintPlayer.SourceGenerators.Tools;
using MintPlayer.SourceGenerators.Tools.ValueComparers;

namespace MintPlayer.Spark.SourceGenerators.Generators;

[Generator(LanguageNames.CSharp)]
public class RecipientRegistrationGenerator : IncrementalGenerator
{
    public override void Initialize(
        IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<Settings> settingsProvider,
        IncrementalValueProvider<ICompilationCache> cacheProvider)
    {
        // Find all classes that implement IRecipient<TMessage>
        var recipientClassesProvider = context.SyntaxProvider
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

                    // Walk AllInterfaces looking for IRecipient<TMessage>
                    var results = new List<RecipientClassInfo>();
                    foreach (var iface in classSymbol.AllInterfaces)
                    {
                        if (iface.IsGenericType &&
                            iface.ConstructedFrom.ToDisplayString() == "MintPlayer.Spark.Messaging.Abstractions.IRecipient<TMessage>")
                        {
                            var messageType = iface.TypeArguments.FirstOrDefault();
                            if (messageType != null)
                            {
                                results.Add(new RecipientClassInfo
                                {
                                    RecipientTypeName = classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                                    MessageTypeName = messageType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                                });
                            }
                        }
                    }

                    return results.Count > 0 ? results.ToArray() : default;
                })
            .Where(static x => x != null)
            .SelectMany(static (x, ct) => x!)
            .Collect();

        // Check if project references MintPlayer.Spark.Messaging
        var knowsMessagingProvider = context.CompilationProvider
            .Select((compilation, ct) =>
                compilation.GetTypeByMetadataName("MintPlayer.Spark.Messaging.SparkMessagingExtensions") != null);

        // Combine and produce the source
        var sourceProvider = recipientClassesProvider
            .Combine(knowsMessagingProvider)
            .Combine(settingsProvider)
            .Select(static (providers, ct) =>
            {
                var recipientClasses = providers.Left.Left;
                var knowsMessaging = providers.Left.Right;
                var settings = providers.Right;

                return (Producer)new RecipientRegistrationProducer(
                    recipientClasses.Where(x => x != null).Cast<RecipientClassInfo>(),
                    knowsMessaging,
                    settings.RootNamespace ?? "GeneratedCode");
            });

        context.ProduceCode(sourceProvider);
    }
}
