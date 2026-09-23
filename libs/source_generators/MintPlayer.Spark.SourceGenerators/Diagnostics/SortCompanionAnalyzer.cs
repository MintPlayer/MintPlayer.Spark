using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using MintPlayer.Spark.SourceGenerators.Models;
using MintPlayer.Spark.SourceGenerators.Naming;
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

    /// <summary>Kept in step with <c>GenerateIndexGenerator.HandWrittenProducer.IndexSearchFieldsMethod</c>.</summary>
    private const string IndexSearchFieldsMethodName = "IndexSearchFields";
    private const string SparkIndexCreationTaskFullName = "MintPlayer.Spark.SparkIndexCreationTask<TDocument>";
    private const string SparkMultiMapIndexCreationTaskFullName = "MintPlayer.Spark.SparkMultiMapIndexCreationTask<TReduceResult>";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [MissingSortCompanionRule, UnassignedSortCompanionRule, UncalledIndexSearchFieldsRule];

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

        ReportUncalledIndexSearchFields(context, indexEntity, indexType);

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

            // ⚠️ "Search", not "Sort". The analyzed copy now lives on {Name}Search and the base field
            // is left plain; the roles were swapped so that equality and ordering work on the name
            // every path uses. SPARK005/006 still ask the same question — is there a companion, and is
            // it assigned — because a hand-written index that declares a field Search without giving
            // it a separate companion still destroys equality on the field it declared.
            var companionName = field + "Search";

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

    /// <summary>
    /// SPARK018: the index has generated field options but no constructor applies them.
    /// </summary>
    /// <remarks>
    /// Reports at the <em>index</em>'s location rather than the projection's, because that is where the
    /// missing call belongs. Both are application-owned — the index class, its constructor, the
    /// <c>[FromIndex]</c> projection and its <c>[Search]</c> attributes all live together — so this
    /// never has to point across a project boundary, where <c>ReportDiagnostic</c> throws and takes the
    /// whole symbol action with it.
    /// </remarks>
    private static void ReportUncalledIndexSearchFields(
        SymbolAnalysisContext context,
        INamedTypeSymbol indexEntity,
        INamedTypeSymbol indexType)
    {
        // Deriving from SparkIndexCreationTask<T> means the call site is supplied by the base class.
        // Exempt, not flagged: there is no constructor call to look for, and demanding one would be
        // demanding the very duplication that throws at deploy.
        if (RavenIndexHierarchy.DerivesFrom(indexType, SparkIndexCreationTaskFullName)
            || RavenIndexHierarchy.DerivesFrom(indexType, SparkMultiMapIndexCreationTaskFullName))
            return;

        // Nothing is generated for an index class that is not partial; SPARK_INDEX_001 covers that.
        var declarations = indexType.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax(context.CancellationToken))
            .OfType<ClassDeclarationSyntax>()
            .ToList();
        if (declarations.Count == 0) return;
        if (!declarations.Any(d => d.Modifiers.Any(SyntaxKind.PartialKeyword))) return;

        var trigger = indexEntity.GetMembers().OfType<IPropertySymbol>()
            .FirstOrDefault(p => !p.GetAttributes().Any(IsIgnoreProperty)
                && p.NeedsGeneratedIndexFieldOptions());
        if (trigger is null) return;

        // "Does EVERY constructor call it" — one that does and one that does not is exactly the hole,
        // and an index with no constructor at all has certainly not called it.
        var constructors = IndexConstructors(indexType, context.CancellationToken);
        if (constructors.Count > 0 && constructors.All(c => Mentioned(c).Contains(IndexSearchFieldsMethodName)))
            return;

        var location = declarations
            .Select(d => d.Identifier.GetLocation())
            .FirstOrDefault(l => l.IsInSource);
        if (location is null) return;

        context.ReportDiagnostic(Diagnostic.Create(
            UncalledIndexSearchFieldsRule, location,
            indexType.Name, IndexSearchFieldsMethodName, indexEntity.Name));
    }

    /// <summary>Every hand-written constructor of the index, in source.</summary>
    /// <remarks>
    /// ⚠️ A generated partial declares no constructor, so this only ever sees hand-written ones —
    /// which is what makes <see cref="UncalledIndexSearchFieldsRule"/> safe. Widening the scan from
    /// the constructors to the whole class would always match, because the generated half declares
    /// the method itself, and the rule would silently never fire.
    /// </remarks>
    private static List<ConstructorDeclarationSyntax> IndexConstructors(
        INamedTypeSymbol indexType,
        System.Threading.CancellationToken cancellationToken)
        => [.. indexType.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax(cancellationToken))
            .OfType<ClassDeclarationSyntax>()
            .SelectMany(c => c.Members)
            .OfType<ConstructorDeclarationSyntax>()];

    /// <summary>
    /// The first hand-written constructor, for the two rules that only need to read declarations out
    /// of one. Deliberately <c>FirstOrDefault</c>: SPARK005/006 ask "what does this index declare",
    /// which any constructor answers, whereas SPARK018 asks "does <em>every</em> constructor call it".
    /// </summary>
    private static ConstructorDeclarationSyntax? IndexConstructor(
        INamedTypeSymbol indexType,
        System.Threading.CancellationToken cancellationToken)
        => IndexConstructors(indexType, cancellationToken).FirstOrDefault();

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

            // Both Search and Exact, still. Narrowing this to Search was considered and rejected:
            // the reason Exact was in scope was that DateTimeOffset fields were declared Exact and so
            // tripped a rule written for analyzed text, and that cause is now removed at the source --
            // the generator no longer declares a DateTimeOffset Exact at all. What remains is a
            // developer hand-declaring Exact on a string, where the warning is still earned: Exact
            // uses the keyword analyzer, so the field orders case-sensitively by ordinal ("ZZ Top"
            // before "Zeta One"), and a companion is what restores the case-insensitive ordering a
            // grid almost always wants.
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
