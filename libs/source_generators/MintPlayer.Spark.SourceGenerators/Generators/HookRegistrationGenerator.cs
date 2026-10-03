using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MintPlayer.Spark.SourceGenerators.Models;
using MintPlayer.SourceGenerators.Tools;

namespace MintPlayer.Spark.SourceGenerators.Generators;

/// <summary>
/// Emits <c>AddHooks(this ISparkBuilder)</c>, registering every concrete persistence hook (#482) —
/// a class implementing <c>ISparkHook</c> — declared in the project. <c>AddSparkFull</c> calls it.
/// </summary>
[Generator(LanguageNames.CSharp)]
public class HookRegistrationGenerator : IncrementalGenerator
{
    private const string SparkHook = "MintPlayer.Spark.Abstractions.Interceptors.ISparkHook";

    public override void Initialize(
        IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<Settings> settingsProvider)
    {
        var hookClassesProvider = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, ct) => node is ClassDeclarationSyntax classDecl &&
                    classDecl.BaseList != null &&
                    classDecl.BaseList.Types.Count > 0,
                transform: static (ctx, ct) =>
                {
                    if (ctx.Node is not ClassDeclarationSyntax classDeclaration)
                        return default;

                    var classSymbol = ctx.SemanticModel.GetDeclaredSymbol(classDeclaration, ct) as INamedTypeSymbol;
                    // An open generic cannot be registered, and a private nested class cannot be named.
                    if (classSymbol == null || classSymbol.IsAbstract || classSymbol.IsGenericType
                        || classSymbol.DeclaredAccessibility == Accessibility.Private)
                        return default;

                    // An Actions class implementing hooks needs no registration: the framework runs it as its type's hook.
                    if (classSymbol.AllInterfaces.Any(i => i.OriginalDefinition.ToDisplayString() == "MintPlayer.Spark.Actions.IPersistentObjectActions<T>"))
                        return default;

                    foreach (var iface in classSymbol.AllInterfaces)
                    {
                        if (iface.ToDisplayString() == SparkHook)
                        {
                            return new HookClassInfo
                            {
                                HookTypeName = classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                            };
                        }
                    }

                    return default;
                })
            .Where(static x => x != null)
            .Collect();

        // Only emit when the project can call AddHook, i.e. references MintPlayer.Spark.Abstractions.
        var knowsHooksProvider = context.CompilationProvider
            .Select((compilation, ct) =>
                compilation.GetTypeByMetadataName("MintPlayer.Spark.Abstractions.Interceptors.SparkBuilderHookExtensions") != null);

        var sourceProvider = hookClassesProvider
            .Combine(knowsHooksProvider)
            .Combine(settingsProvider)
            .Select(static (providers, ct) =>
            {
                var hookClasses = providers.Left.Left;
                var knowsHooks = providers.Left.Right;
                var settings = providers.Right;

                return (Producer)new HookRegistrationProducer(
                    hookClasses.Where(x => x != null).Cast<HookClassInfo>(),
                    knowsHooks,
                    settings.RootNamespace ?? "GeneratedCode");
            });

        context.ProduceCode(sourceProvider);
    }
}
