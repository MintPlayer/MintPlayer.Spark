using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MintPlayer.Spark.SourceGenerators.Models;
using MintPlayer.SourceGenerators.Tools;
using MintPlayer.SourceGenerators.Tools.ValueComparers;

namespace MintPlayer.Spark.SourceGenerators.Generators;

/// <summary>
/// Emits one <c>[assembly: SparkAttributeDescription(typeof(T), "Prop", "summary")]</c> per documented
/// public read/write property, so <c>--spark-synchronize-model</c> can seed attribute descriptions from
/// <c>///</c> summaries (#348).
/// </summary>
/// <remarks>
/// <para>
/// Every class in the compilation is a candidate, not only entities. Entities are plain POCOs with no
/// marker, and the type that makes them entities (the <c>SparkContext</c> or an <c>Actions</c> class)
/// usually lives in the host project while the entity library only has the comments — so the entity
/// library's compilation cannot know which of its classes are entities. Over-emitting is harmless: the
/// attribute is <c>[Conditional("DEBUG")]</c>, so none of it reaches a Release build, and the
/// synchronizer only looks up the pairs it reflects over.
/// </para>
/// <para>
/// Emits nothing when the compilation does not reference an Abstractions that has the attribute type.
/// </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public class AttributeDescriptionsGenerator : IncrementalGenerator
{
    private const string AttributeMetadataName = "MintPlayer.Spark.Abstractions.SparkAttributeDescriptionAttribute";
    private const string IgnorePropertyMetadataName = "MintPlayer.Spark.Abstractions.IgnorePropertyAttribute";

    public override void Initialize(
        IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<Settings> settingsProvider,
        IncrementalValueProvider<ICompilationCache> cacheProvider)
    {
        var descriptionsProvider = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, ct) => node is PropertyDeclarationSyntax property && HasDocComment(property),
                transform: static (ctx, ct) =>
                {
                    if (ctx.Node is not PropertyDeclarationSyntax declaration)
                        return default;

                    if (ctx.SemanticModel.GetDeclaredSymbol(declaration, ct) is not IPropertySymbol property)
                        return default;

                    if (property.IsStatic || property.IsIndexer ||
                        property.DeclaredAccessibility != Accessibility.Public ||
                        property.GetMethod is null || property.SetMethod is null)
                        return default;

                    if (property.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == IgnorePropertyMetadataName))
                        return default;

                    if (property.ContainingType is not INamedTypeSymbol type || type.IsStatic)
                        return default;

                    var typeOf = TypeOfExpression(type);
                    if (typeOf is null)
                        return default;

                    var summary = XmlDocSummary.For(property, declaration, ct);
                    if (summary is null)
                        return default;

                    return new AttributeDescriptionInfo
                    {
                        TypeOfExpression = typeOf,
                        TypeSortKey = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                        PropertyName = property.Name,
                        Summary = summary,
                    };
                })
            .Where(static x => x != null)
            .WithNullableComparer()
            .Collect();

        var knowsSparkProvider = context.CompilationProvider
            .Select((compilation, ct) => compilation.GetTypeByMetadataName(AttributeMetadataName) != null);

        var sourceProvider = descriptionsProvider
            .Combine(knowsSparkProvider)
            .Combine(settingsProvider)
            .Select(static (providers, ct) =>
            {
                var descriptions = providers.Left.Left;
                var knowsSpark = providers.Left.Right;
                var settings = providers.Right;

                return (Producer)new AttributeDescriptionsProducer(
                    descriptions.Where(x => x != null).Cast<AttributeDescriptionInfo>(),
                    knowsSpark,
                    settings.RootNamespace ?? "GeneratedCode");
            });

        context.ProduceCode(sourceProvider);
    }

    /// <summary>Cheap syntactic pre-filter: any <c>///</c> (structured or plain) in the leading trivia.</summary>
    private static bool HasDocComment(PropertyDeclarationSyntax property)
    {
        foreach (var trivia in property.GetLeadingTrivia())
        {
            if (trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
                return true;

            if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) &&
                trivia.ToString().StartsWith("///", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// <c>global::Ns.Outer.Inner</c>, or the unbound form <c>global::Ns.Box&lt;&gt;</c> for a generic type.
    /// <see langword="null"/> when a containing type is generic (no unbound <c>typeof</c> spelling exists
    /// for a nested type of an open generic) — such properties are skipped.
    /// </summary>
    private static string? TypeOfExpression(INamedTypeSymbol type)
    {
        for (var outer = type.ContainingType; outer != null; outer = outer.ContainingType)
        {
            if (outer.IsGenericType)
                return null;
        }

        if (!type.IsGenericType)
            return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        var unbound = type.ConstructUnboundGenericType();
        return unbound.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }
}
