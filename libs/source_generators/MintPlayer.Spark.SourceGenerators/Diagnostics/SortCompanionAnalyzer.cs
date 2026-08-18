using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

/// <summary>
/// Guards the sort-companion convention in <strong>hand-written</strong> indexes.
/// <para>
/// A field indexed <c>FieldIndexing.Search</c> is analyzed and tokenized, so ordering on it is meaningless.
/// The repair is a companion field carrying the same value with no indexing declared. A generated index/index-entity
/// pair gets that automatically; a hand-written one is left to discipline, and this is what replaces the
/// discipline.
/// </para>
/// <para>
/// It needs no suppression mechanism. Analyzers run <em>after</em> generators within a single compilation, and
/// a partial type with at least one hand-written declaration is analyzed normally while its generated members
/// are still present in the symbol model — so wherever the generator contributed a companion, this simply finds
/// it and stays quiet. Referencing the generator therefore turns the suggestions off by itself.
/// </para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed partial class SortCompanionAnalyzer : DiagnosticAnalyzer
{
    private const string FromIndexAttributeFullName = "MintPlayer.Spark.Abstractions.FromIndexAttribute";
    private const string IgnorePropertyAttributeFullName = "MintPlayer.Spark.Abstractions.IgnorePropertyAttribute";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [MissingSortCompanionRule, UnassignedSortCompanionRule];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();

        // Generated pairs are correct by construction, and their diagnostics would be unfixable anyway.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

        context.RegisterSymbolAction(AnalyzeIndexEntity, SymbolKind.NamedType);
    }

    private static void AnalyzeIndexEntity(SymbolAnalysisContext context)
    {
        if (context.Symbol is not INamedTypeSymbol indexEntity) return;

        var fromIndex = indexEntity.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.ToDisplayString() == FromIndexAttributeFullName);
        if (fromIndex is null) return;

        if (fromIndex.ConstructorArguments.Length == 0) return;
        if (fromIndex.ConstructorArguments[0].Value is not INamedTypeSymbol indexType) return;

        var constructor = IndexConstructor(indexType, context.CancellationToken);
        if (constructor is null) return;

        var analyzedFields = AnalyzedFields(constructor);
        if (analyzedFields.Count == 0) return;

        var properties = indexEntity.GetMembers().OfType<IPropertySymbol>().ToList();
        var propertyNames = new HashSet<string>(properties.Select(p => p.Name), System.StringComparer.Ordinal);

        // Every identifier mentioned anywhere in the index constructor. Deliberately coarse: a companion that
        // appears nowhere in the constructor is definitely unassigned, whereas trying to parse only the map's
        // initializer would misjudge the `let`, ternary and helper-method shapes real indexes use.
        var mentioned = Mentioned(constructor);

        foreach (var field in analyzedFields)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var companionName = field + "Sort";

            if (!propertyNames.Contains(companionName))
            {
                var target = properties.FirstOrDefault(p => p.Name == field);
                var location = target?.Locations.FirstOrDefault(l => l.IsInSource)
                    ?? indexEntity.Locations.FirstOrDefault(l => l.IsInSource);

                // Never Location.None or a generated location: ConfigureGeneratedCodeAnalysis suppresses
                // diagnostics BY LOCATION, so either would be dropped without a trace.
                if (location is not null)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        MissingSortCompanionRule, location, field, indexEntity.Name, companionName));
                }

                continue;
            }

            if (!mentioned.Contains(companionName))
            {
                var companion = properties.First(p => p.Name == companionName);
                var location = companion.Locations.FirstOrDefault(l => l.IsInSource);
                if (location is not null)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        UnassignedSortCompanionRule, location, companionName, indexType.Name));
                }
            }
        }
    }

    /// <summary>The hand-written constructor of the index, or <c>null</c> if it has none in source.</summary>
    private static ConstructorDeclarationSyntax? IndexConstructor(
        INamedTypeSymbol indexType,
        System.Threading.CancellationToken cancellationToken)
        => indexType.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax(cancellationToken))
            .OfType<ClassDeclarationSyntax>()
            .SelectMany(c => c.Members)
            .OfType<ConstructorDeclarationSyntax>()
            .FirstOrDefault();

    /// <summary>
    /// Field names the index declares as analyzed or exact — the ones whose ordering needs a companion.
    /// Matches <c>Index(nameof(VCar.Model), FieldIndexing.Search)</c> and the <c>Exact</c> form.
    /// </summary>
    private static List<string> AnalyzedFields(ConstructorDeclarationSyntax constructor)
    {
        var fields = new List<string>();

        foreach (var invocation in constructor.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var name = invocation.Expression switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.Text,
                MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
                _ => null,
            };

            if (name != "Index") continue;
            if (invocation.ArgumentList.Arguments.Count < 2) continue;

            var indexing = invocation.ArgumentList.Arguments[1].Expression.ToString();
            if (!indexing.EndsWith("Search") && !indexing.EndsWith("Exact")) continue;

            if (FieldNameOf(invocation.ArgumentList.Arguments[0].Expression) is { } field)
                fields.Add(field);
        }

        return fields;
    }

    /// <summary>
    /// The field name from an <c>Index(...)</c> first argument, whether written as
    /// <c>nameof(VCar.Model)</c>, <c>nameof(Model)</c> or the literal <c>"Model"</c>.
    /// </summary>
    private static string? FieldNameOf(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression)
            => literal.Token.ValueText,
        InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "nameof" } } nameOf
            when nameOf.ArgumentList.Arguments.Count == 1
            => nameOf.ArgumentList.Arguments[0].Expression switch
            {
                MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
                IdentifierNameSyntax identifier => identifier.Identifier.Text,
                _ => null,
            },
        _ => null,
    };

    private static HashSet<string> Mentioned(ConstructorDeclarationSyntax constructor)
    {
        var names = new HashSet<string>(System.StringComparer.Ordinal);

        foreach (var token in constructor.DescendantTokens())
        {
            if (token.IsKind(SyntaxKind.IdentifierToken))
                names.Add(token.ValueText);
        }

        return names;
    }

    internal static bool IsIgnoreProperty(AttributeData attribute)
        => attribute.AttributeClass?.ToDisplayString() == IgnorePropertyAttributeFullName;
}
