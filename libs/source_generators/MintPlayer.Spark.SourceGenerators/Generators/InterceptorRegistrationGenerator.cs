using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MintPlayer.Spark.SourceGenerators.Models;
using MintPlayer.SourceGenerators.Tools;

namespace MintPlayer.Spark.SourceGenerators.Generators;

/// <summary>
/// Emits <c>AddInterceptors(this ISparkBuilder)</c>, registering every concrete persistence interceptor (#482) —
/// a class implementing <c>ISparkInterceptor</c> — declared in the project. <c>AddSparkFull</c> calls it.
/// </summary>
[Generator(LanguageNames.CSharp)]
public class InterceptorRegistrationGenerator : IncrementalGenerator
{
    private const string SparkInterceptor = "MintPlayer.Spark.Abstractions.Interceptors.ISparkInterceptor";

    public override void Initialize(
        IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<Settings> settingsProvider)
    {
        var interceptorClassesProvider = context.SyntaxProvider
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

                    // An Actions class implementing interceptors needs no registration: the framework runs it as its type's interceptor.
                    if (classSymbol.AllInterfaces.Any(i => i.OriginalDefinition.ToDisplayString() == "MintPlayer.Spark.Actions.IPersistentObjectActions<T>"))
                        return default;

                    foreach (var iface in classSymbol.AllInterfaces)
                    {
                        if (iface.ToDisplayString() == SparkInterceptor)
                        {
                            return new InterceptorClassInfo
                            {
                                InterceptorTypeName = classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                            };
                        }
                    }

                    return default;
                })
            .Where(static x => x != null)
            .Collect();

        // Only emit when the project can call AddInterceptor, i.e. references MintPlayer.Spark.Abstractions.
        var knowsInterceptorsProvider = context.CompilationProvider
            .Select((compilation, ct) =>
                compilation.GetTypeByMetadataName("MintPlayer.Spark.Abstractions.Interceptors.SparkBuilderInterceptorExtensions") != null);

        var sourceProvider = interceptorClassesProvider
            .Combine(knowsInterceptorsProvider)
            .Combine(settingsProvider)
            .Select(static (providers, ct) =>
            {
                var interceptorClasses = providers.Left.Left;
                var knowsInterceptors = providers.Left.Right;
                var settings = providers.Right;

                return (Producer)new InterceptorRegistrationProducer(
                    interceptorClasses.Where(x => x != null).Cast<InterceptorClassInfo>(),
                    knowsInterceptors,
                    settings.RootNamespace ?? "GeneratedCode");
            });

        context.ProduceCode(sourceProvider);
    }
}
