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

        var properties = indexEntity.GetMembers().OfType<IPropertySymbol>().ToList();
        var propertyNames = new HashSet<string>(properties.Select(p => p.Name), System.StringComparer.Ordinal);

        var analyzedFields = AnalyzedFields(constructor);

        ReportMissingCompanions(context, indexEntity, analyzedFields, properties, propertyNames);
        ReportUnassignedCompanions(context, indexEntity, indexType, analyzedFields, properties);
    }

    /// <summary>SPARK005: a field the index declares Search or Exact has no companion at all.</summary>
    private static void ReportMissingCompanions(
        SymbolAnalysisContext context,
        INamedTypeSymbol indexEntity,
        List<(string Field, bool IsExact)> analyzedFields,
        List<IPropertySymbol> properties,
        HashSet<string> propertyNames)
    {
        foreach (var (field, isExact) in analyzedFields)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            // Strings only. A DateTimeOffset (or any other non-string) indexes as one canonical term
            // whatever its indexing and orders correctly without a companion — measured for default,
            // Exact and Search on RavenDB 7.2.6, Corax and Lucene (docs/datetimeoffset_query_sort_filter_PRD.md,
            // SP-270). A field that is not on the projection cannot be typed, and keeps the warning.
            var fieldProperty = properties.FirstOrDefault(p => p.Name == field);
            if (fieldProperty is not null && fieldProperty.Type.SpecialType != SpecialType.System_String)
                continue;

            // The field IS the analyzed copy: `Index(nameof(V.ModelSearch), Search)` beside a plain
            // `Model` is the convention itself — exactly what the generator emits, and what the SPARK005
            // fix produces. Asking it for a 'ModelSearchSearch' was this rule flagging its own remedy;
            // found by the fix tests re-analyzing their output (docs/datetimeoffset_query_sort_filter_PRD.md, T34).
            if (!isExact && field.Length > "Search".Length && field.EndsWith("Search", System.StringComparison.Ordinal)
                && propertyNames.Contains(field.Substring(0, field.Length - "Search".Length)))
                continue;

            // ⚠️ The companion depends on why the field needs one.
            // - Search: "Search", not "Sort". The analyzed copy lives on {Name}Search and the base field
            //   is left plain; the roles were swapped so that equality and ordering work on the name
            //   every path uses. A hand-written index that declares a field Search without a separate
            //   companion still destroys equality on the field it declared.
            // - Exact: "Sort". The field is already one term, but ordered by ordinal; the remedy is a
            //   plain copy that QueryExecutor.ResolveSortProperty orders by instead. Asking for a
            //   {Name}Search here, as this rule once did, prescribed an analyzed copy that sorts nothing.
            var companionName = field + (isExact ? "Sort" : "Search");

            if (propertyNames.Contains(companionName)) continue;

            // Never Location.None or a generated location: ConfigureGeneratedCodeAnalysis suppresses
            // diagnostics BY LOCATION, so either would be dropped without a trace.
            var location = HandWritten(fieldProperty) ?? HandWritten(indexEntity);
            if (location is null) continue;

            context.ReportDiagnostic(Diagnostic.Create(
                MissingSortCompanionRule, location,
                FixProperties(field, companionName, isExact ? CompanionKindSort : CompanionKindSearch),
                field, indexEntity.Name, companionName));
        }
    }

    /// <summary>
    /// SPARK006: a companion the projection declares — hand-written, or generated onto a partial
    /// projection — that no hand-written index constructor ever assigns.
    /// </summary>
    /// <remarks>
    /// Widened 2026-10-05 (docs/datetimeoffset_query_sort_filter_PRD.md, D15). It used to look only at
    /// companions of fields found by the hand-written <c>Index(...)</c> scan, and it reported at the
    /// companion — which, when the generator declared it, is a generated location and was dropped. So
    /// the rule never fired for the <c>[Search]</c>/<c>IndexSearchFields()</c> shape or for a
    /// <c>{Name}Raw</c> at all. Measured cost of an unmapped companion (SP5): a <c>{Name}Raw</c> returns
    /// every <c>DateTimeOffset</c> at <c>+00:00</c>, a <c>{Name}Search</c> makes search find nothing —
    /// with a healthy index, correct sorting, and no warning anywhere.
    /// </remarks>
    private static void ReportUnassignedCompanions(
        SymbolAnalysisContext context,
        INamedTypeSymbol indexEntity,
        INamedTypeSymbol indexType,
        List<(string Field, bool IsExact)> analyzedFields,
        List<IPropertySymbol> properties)
    {
        // Every hand-written constructor counts: a companion assigned in any of them is assigned.
        var mentioned = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var constructor in IndexConstructors(indexType, context.CancellationToken))
            mentioned.UnionWith(MentionedOutsideFieldOptions(constructor));

        var declaredByIndexScan = new HashSet<string>(
            analyzedFields.Select(f => f.Field + (f.IsExact ? "Sort" : "Search")), System.StringComparer.Ordinal);

        foreach (var companion in properties)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            if (CompanionOf(companion, properties, declaredByIndexScan) is not { } found) continue;
            if (mentioned.Contains(companion.Name)) continue;
            var (baseProperty, kind) = found;

            // Report on the base field the author wrote, so a generated companion is still reachable.
            var location = HandWritten(baseProperty) ?? HandWritten(companion) ?? HandWritten(indexEntity);
            if (location is null) continue;

            context.ReportDiagnostic(Diagnostic.Create(
                UnassignedSortCompanionRule, location,
                FixProperties(baseProperty.Name, companion.Name, kind),
                companion.Name, indexType.Name, ConsequenceOf(kind, baseProperty.Name), baseProperty.Name));
        }
    }

    internal const string FieldProperty = "Field";
    internal const string CompanionProperty = "Companion";
    internal const string KindProperty = "Kind";
    internal const string CompanionKindSearch = "Search";
    internal const string CompanionKindSort = "Sort";
    internal const string CompanionKindRaw = "Raw";

    private static ImmutableDictionary<string, string?> FixProperties(string field, string companion, string kind)
        => ImmutableDictionary<string, string?>.Empty
            .Add(FieldProperty, field)
            .Add(CompanionProperty, companion)
            .Add(KindProperty, kind);

    private static string ConsequenceOf(string kind, string field) => kind switch
    {
        CompanionKindRaw => $"every '{field}' is read back from the index at offset +00:00",
        CompanionKindSearch => "full-text search on it matches nothing",
        _ => "ordering by it does nothing",
    };

    /// <summary>
    /// The base property and kind when <paramref name="property"/> is an index companion: named
    /// <c>{F}Search</c>, <c>{F}Sort</c> or <c>{F}Raw</c> with <c>F</c> also on the projection, and either
    /// <c>[IgnoreProperty]</c> (as every generated companion is) or named by the index's own
    /// <c>Index(...)</c> declarations. Anything else is a domain property that merely ends in one of
    /// those words.
    /// </summary>
    private static (IPropertySymbol Base, string Kind)? CompanionOf(
        IPropertySymbol property, List<IPropertySymbol> properties, HashSet<string> declaredByIndexScan)
    {
        foreach (var kind in new[] { CompanionKindSearch, CompanionKindSort, CompanionKindRaw })
        {
            if (property.Name.Length <= kind.Length || !property.Name.EndsWith(kind, System.StringComparison.Ordinal))
                continue;

            var baseName = property.Name.Substring(0, property.Name.Length - kind.Length);
            var baseProperty = properties.FirstOrDefault(p => p.Name == baseName);
            if (baseProperty is null) continue;

            if (property.GetAttributes().Any(IsIgnoreProperty) || declaredByIndexScan.Contains(property.Name))
                return (baseProperty, kind);
        }

        return null;
    }

    /// <summary>
    /// The first source location of <paramref name="symbol"/> that is not generated code — the only kind
    /// a diagnostic survives at under <see cref="GeneratedCodeAnalysisFlags.None"/>.
    /// </summary>
    private static Location? HandWritten(ISymbol? symbol)
        => symbol?.Locations.FirstOrDefault(l =>
            l.IsInSource && !l.SourceTree!.FilePath.EndsWith(".g.cs", System.StringComparison.OrdinalIgnoreCase));

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
    /// Field names the index declares as analyzed or exact — the ones whose ordering needs a companion —
    /// and whether the declaration was <c>Exact</c>. Matches <c>Index(nameof(VCar.Model), FieldIndexing.Search)</c>
    /// and the <c>Exact</c> form.
    /// </summary>
    private static List<(string Field, bool IsExact)> AnalyzedFields(ConstructorDeclarationSyntax constructor)
    {
        var fields = new List<(string Field, bool IsExact)>();

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
            // grid almost always wants. A hand-declared Exact on a DateTimeOffset is filtered out by
            // type in the caller: it orders by instant regardless (PRD SP-270).
            var indexing = invocation.ArgumentList.Arguments[1].Expression.ToString();
            var isExact = indexing.EndsWith("Exact");
            if (!indexing.EndsWith("Search") && !isExact) continue;

            if (FieldNameOf(invocation.ArgumentList.Arguments[0].Expression) is { } field)
                fields.Add((field, isExact));
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

    /// <summary>
    /// The field-option calls whose arguments name a field without assigning it. A companion that only
    /// appears in <c>Index(nameof(V.FRaw), FieldIndexing.No)</c> is declared, not mapped — measured in
    /// SP5, where exactly that index deployed healthy and returned <c>+00:00</c> for every row.
    /// </summary>
    private static readonly HashSet<string> FieldOptionCalls = new(System.StringComparer.Ordinal)
    {
        "Index", "Store", "Analyze", "Suggestion", "TermVector", "Spatial", "IndexSearchFields",
    };

    /// <summary>
    /// <see cref="Mentioned"/>, minus identifiers that only occur inside a field-option call's arguments.
    /// </summary>
    private static HashSet<string> MentionedOutsideFieldOptions(ConstructorDeclarationSyntax constructor)
    {
        var names = new HashSet<string>(System.StringComparer.Ordinal);

        foreach (var token in constructor.DescendantTokens())
        {
            if (!token.IsKind(SyntaxKind.IdentifierToken)) continue;

            var insideFieldOption = token.Parent?.AncestorsAndSelf()
                .OfType<ArgumentListSyntax>()
                .Any(args => args.Parent is InvocationExpressionSyntax invocation
                    && invocation.Expression switch
                    {
                        IdentifierNameSyntax id => FieldOptionCalls.Contains(id.Identifier.Text),
                        GenericNameSyntax generic => FieldOptionCalls.Contains(generic.Identifier.Text),
                        MemberAccessExpressionSyntax member => FieldOptionCalls.Contains(member.Name.Identifier.Text),
                        _ => false,
                    }) ?? false;

            if (!insideFieldOption)
                names.Add(token.ValueText);
        }

        return names;
    }

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
