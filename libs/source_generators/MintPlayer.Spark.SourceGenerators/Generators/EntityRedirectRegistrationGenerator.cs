using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MintPlayer.Spark.SourceGenerators.Models;
using MintPlayer.SourceGenerators.Tools;

namespace MintPlayer.Spark.SourceGenerators.Generators;

/// <summary>
/// Emits <c>spark.AddEntityRedirects()</c>, which registers every <c>ISparkEntityRedirect</c> in the project (#602,
/// tier c), so a new redirect hook needs no line in Program.cs.
/// </summary>
[Generator(LanguageNames.CSharp)]
public class EntityRedirectRegistrationGenerator : IncrementalGenerator
{
    public override void Initialize(
        IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<Settings> settingsProvider)
    {
        var redirectClassesProvider = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, ct) => node is ClassDeclarationSyntax classDecl &&
                    classDecl.BaseList != null &&
                    classDecl.BaseList.Types.Count > 0,
                transform: static (ctx, ct) =>
                {
                    if (ctx.Node is not ClassDeclarationSyntax classDeclaration)
                        return default;

                    var classSymbol = ctx.SemanticModel.GetDeclaredSymbol(classDeclaration, ct) as INamedTypeSymbol;
                    if (!EntityRedirectFilter.IsRegistrable(classSymbol))
                        return default;

                    return new EntityRedirectClassInfo
                    {
                        RedirectTypeName = classSymbol!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                    };
                })
            .Where(static x => x != null)
            .Collect();

        // Only emit when AddEntityRedirect<T> exists to be called.
        var knowsRedirectsProvider = context.CompilationProvider
            .Select(static (compilation, ct) => compilation.GetTypeByMetadataName(EntityRedirectFilter.RegistrationExtensions) != null);

        var sourceProvider = redirectClassesProvider
            .Combine(knowsRedirectsProvider)
            .Combine(settingsProvider)
            .Select(static (providers, ct) => (Producer)new EntityRedirectRegistrationProducer(
                providers.Left.Left.Where(x => x != null).Cast<EntityRedirectClassInfo>(),
                providers.Left.Right,
                providers.Right.RootNamespace ?? "GeneratedCode"));

        context.ProduceCode(sourceProvider);
    }
}
