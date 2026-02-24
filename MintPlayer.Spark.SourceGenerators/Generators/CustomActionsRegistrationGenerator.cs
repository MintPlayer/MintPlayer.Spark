using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MintPlayer.Spark.SourceGenerators.Models;
using MintPlayer.SourceGenerators.Tools;
using MintPlayer.SourceGenerators.Tools.ValueComparers;

namespace MintPlayer.Spark.SourceGenerators.Generators;

[Generator(LanguageNames.CSharp)]
public class CustomActionsRegistrationGenerator : IncrementalGenerator
{
    public override void Initialize(
        IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<Settings> settingsProvider,
        IncrementalValueProvider<ICompilationCache> cacheProvider)
    {
        // Find all classes that implement ICustomAction (directly or via SparkCustomAction)
        var customActionClassesProvider = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, ct) => node is ClassDeclarationSyntax classDecl &&
                    !classDecl.Modifiers.Any(m => m.Text == "abstract") &&
                    (classDecl.BaseList != null && classDecl.BaseList.Types.Count > 0),
                transform: static (ctx, ct) =>
                {
                    if (ctx.Node is not ClassDeclarationSyntax classDeclaration)
                        return default;

                    var semanticModel = ctx.SemanticModel;
                    var classSymbol = semanticModel.GetDeclaredSymbol(classDeclaration, ct) as INamedTypeSymbol;
                    if (classSymbol == null || classSymbol.IsAbstract)
                        return default;

                    // Check if this class implements ICustomAction
                    if (!ImplementsICustomAction(classSymbol))
                        return default;

                    // Derive action name: strip optional "Action" suffix
                    var className = classSymbol.Name;
                    var actionName = className.EndsWith("Action")
                        ? className.Substring(0, className.Length - 6)
                        : className;

                    return new CustomActionClassInfo
                    {
                        TypeName = classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                        ActionName = actionName,
                    };
                })
            .Where(static x => x != null)
            .Collect();

        // Check if project references MintPlayer.Spark.Abstractions (where ICustomAction lives)
        var knowsCustomActionProvider = context.CompilationProvider
            .Select((compilation, ct) =>
                compilation.GetTypeByMetadataName("MintPlayer.Spark.Abstractions.Actions.ICustomAction") != null);

        // Combine and produce the source
        var sourceProvider = customActionClassesProvider
            .Combine(knowsCustomActionProvider)
            .Combine(settingsProvider)
            .Select(static (providers, ct) =>
            {
                var actionClasses = providers.Left.Left;
                var knowsCustomAction = providers.Left.Right;
                var settings = providers.Right;

                return (Producer)new CustomActionsRegistrationProducer(
                    actionClasses.Where(x => x != null).Cast<CustomActionClassInfo>(),
                    knowsCustomAction,
                    settings.RootNamespace ?? "GeneratedCode");
            });

        context.ProduceCode(sourceProvider);
    }

    private static bool ImplementsICustomAction(INamedTypeSymbol classSymbol)
    {
        foreach (var iface in classSymbol.AllInterfaces)
        {
            if (iface.ToDisplayString() == "MintPlayer.Spark.Abstractions.Actions.ICustomAction")
                return true;
        }
        return false;
    }
}
