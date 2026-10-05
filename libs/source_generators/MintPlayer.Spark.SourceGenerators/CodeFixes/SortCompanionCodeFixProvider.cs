using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using MintPlayer.Spark.SourceGenerators.Diagnostics;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MintPlayer.Spark.SourceGenerators.CodeFixes;

/// <summary>
/// Fixes for a hand-written index's companions (issue #270; docs/datetimeoffset_query_sort_filter_PRD.md
/// §9, D14 and D16).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>SPARK005, Search</b> — move the analysis to a companion: add <c>[IgnoreProperty] FSearch</c>
/// to the projection, map it from F's expression, and point <c>Index(F, Search)</c> at it. F stays plain,
/// so it sorts and compares again — exactly what the generator emits for <c>[Search]</c>.</item>
/// <item><b>SPARK005, Exact</b> — add and map a plain <c>[IgnoreProperty] FSort</c> copy, which
/// <c>QueryExecutor.ResolveSortProperty</c> orders by.</item>
/// <item><b>SPARK006</b> — map the companion that is declared but never assigned, as the generator
/// would have: <c>FSearch = …</c>, <c>FSort = …</c>, or <c>FRaw = new SparkIndexValue&lt;T&gt; { V = … }</c>.</item>
/// </list>
/// <para>
/// One solution-level change: the projection and the index usually live in different documents, and a
/// fix that adds the property but not the assignment would swap SPARK005 for SPARK006 — worse than no
/// fix. When the map is a shape <see cref="IndexMapEditor"/> refuses, no action is offered at all and
/// the diagnostic stands.
/// </para>
/// <para>IDE-only (D18): <c>dotnet build</c> never loads a fix provider.</para>
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(SortCompanionCodeFixProvider)), Shared]
public sealed class SortCompanionCodeFixProvider : CodeFixProvider
{
    private const string FromIndexAttributeFullName = "MintPlayer.Spark.Abstractions.FromIndexAttribute";
    private const string AbstractionsNamespace = "MintPlayer.Spark.Abstractions";

    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create("SPARK005", "SPARK006");

    // No Fix All: each diagnostic edits its own index map, and a batch fixer would need
    // Microsoft.CodeAnalysis.Features, a far heavier reference than this assembly carries.
    public override FixAllProvider? GetFixAllProvider() => null;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        foreach (var diagnostic in context.Diagnostics)
        {
            if (!diagnostic.Properties.TryGetValue(SortCompanionAnalyzer.FieldProperty, out var field) || field is null) continue;
            if (!diagnostic.Properties.TryGetValue(SortCompanionAnalyzer.CompanionProperty, out var companion) || companion is null) continue;
            if (!diagnostic.Properties.TryGetValue(SortCompanionAnalyzer.KindProperty, out var kind) || kind is null) continue;

            var plan = await PlanAsync(context.Document, diagnostic, field, companion, kind, context.CancellationToken).ConfigureAwait(false);
            if (plan is null) continue;

            var title = diagnostic.Id == "SPARK005"
                ? kind == SortCompanionAnalyzer.CompanionKindSearch
                    ? $"Move search on '{field}' to a '{companion}' companion"
                    : $"Add the sort companion '{companion}'"
                : $"Map '{companion}' in the index";

            context.RegisterCodeFix(
                CodeAction.Create(title, _ => Task.FromResult(plan), equivalenceKey: $"{nameof(SortCompanionCodeFixProvider)}.{diagnostic.Id}.{kind}"),
                diagnostic);
        }
    }

    /// <summary>The fixed solution, or <see langword="null"/> when any part of the fix cannot be made safely.</summary>
    private static async Task<Solution?> PlanAsync(
        Document document, Diagnostic diagnostic, string field, string companion, string kind, CancellationToken cancellationToken)
    {
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (model is null || root is null) return null;

        if (Projection(model, root, diagnostic, cancellationToken) is not { } projection) return null;
        if (IndexOf(projection) is not { } indexType) return null;

        // The hand-written declaration that holds the constructor — a partial index's generated half has none.
        var indexDeclaration = indexType.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax(cancellationToken))
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Members.OfType<ConstructorDeclarationSyntax>().Any());
        if (indexDeclaration is null) return null;

        var solution = document.Project.Solution;
        var indexDocument = solution.GetDocument(indexDeclaration.SyntaxTree);
        if (indexDocument is null) return null;
        var indexText = await indexDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);

        var edits = new Dictionary<DocumentId, List<TextChange>>();
        void Add(DocumentId id, TextChange change)
        {
            if (!edits.TryGetValue(id, out var list)) edits[id] = list = [];
            list.Add(change);
        }

        // 1. The map assignment, in every map.
        if (MakeExpression(projection, companion, kind) is not { } makeExpression) return null;
        var mapChanges = IndexMapEditor.InsertCompanion(indexDeclaration, indexText, field, companion, makeExpression);
        if (mapChanges is null) return null;
        foreach (var change in mapChanges) Add(indexDocument.Id, change);

        if (diagnostic.Id == "SPARK005")
        {
            // 2. The companion property itself, next to F.
            if (await AddPropertyAsync(solution, projection, field, companion, cancellationToken).ConfigureAwait(false) is not { } added)
                return null;
            Add(added.Item1, added.Item2);

            // 3. Search moves to the companion.
            if (kind == SortCompanionAnalyzer.CompanionKindSearch)
            {
                if (IndexMapEditor.RetargetSearchIndex(indexDeclaration, field, companion) is not { } retarget) return null;
                Add(indexDocument.Id, retarget);
            }
        }

        if (edits.Count == 0) return null;

        foreach (var pair in edits)
        {
            var target = solution.GetDocument(pair.Key)!;
            var text = await target.GetTextAsync(cancellationToken).ConfigureAwait(false);
            solution = solution.WithDocumentText(pair.Key, text.WithChanges(pair.Value.OrderBy(c => c.Span.Start)));
        }

        return solution;
    }

    /// <summary>The <c>[FromIndex]</c> projection the diagnostic sits on: on F, or on the type itself.</summary>
    private static INamedTypeSymbol? Projection(SemanticModel model, SyntaxNode root, Diagnostic diagnostic, CancellationToken cancellationToken)
    {
        var span = diagnostic.Location.SourceSpan;
        if (span.End > root.FullSpan.End) return null;

        var node = root.FindToken(span.Start).Parent;
        for (; node is not null; node = node.Parent)
        {
            var symbol = node switch
            {
                PropertyDeclarationSyntax property => model.GetDeclaredSymbol(property, cancellationToken)?.ContainingType,
                TypeDeclarationSyntax type => model.GetDeclaredSymbol(type, cancellationToken) as INamedTypeSymbol,
                _ => null,
            };
            if (symbol is not null) return symbol;
        }

        return null;
    }

    private static INamedTypeSymbol? IndexOf(INamedTypeSymbol projection)
        => projection.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == FromIndexAttributeFullName)?
            .ConstructorArguments.FirstOrDefault().Value as INamedTypeSymbol;

    /// <summary>
    /// What the companion is assigned from F's expression — the generator's exact text
    /// (GenerateIndexGenerator.cs, the Search copy and the Raw wrapper).
    /// </summary>
    private static Func<string, string>? MakeExpression(INamedTypeSymbol projection, string companion, string kind)
    {
        if (kind != SortCompanionAnalyzer.CompanionKindRaw) return expression => expression;

        // SparkIndexValue<T>, nullable or not: T decides the wrapper, nullability mirrored exactly.
        var wrapper = projection.GetMembers(companion).OfType<IPropertySymbol>().FirstOrDefault()?.Type as INamedTypeSymbol;
        if (wrapper is not { TypeArguments.Length: 1 }) return null;

        var valueType = wrapper.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat
            .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier));
        return expression => $"new global::{AbstractionsNamespace}.SparkIndexValue<{valueType}> {{ V = {expression} }}";
    }

    /// <summary>
    /// <c>[IgnoreProperty] public &lt;F's type&gt; {companion} { get; set; }</c>, inserted after F's
    /// declaration with F's indentation. The attribute is written short only when the file already
    /// imports the Abstractions namespace.
    /// </summary>
    private static async Task<(DocumentId, TextChange)?> AddPropertyAsync(
        Solution solution, INamedTypeSymbol projection, string field, string companion, CancellationToken cancellationToken)
    {
        var fieldDeclaration = projection.GetMembers(field).OfType<IPropertySymbol>()
            .SelectMany(p => p.DeclaringSyntaxReferences)
            .Select(r => r.GetSyntax(cancellationToken))
            .OfType<PropertyDeclarationSyntax>()
            .FirstOrDefault();
        if (fieldDeclaration is null) return null;

        var document = solution.GetDocument(fieldDeclaration.SyntaxTree);
        if (document is null) return null;
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);

        var compilationUnit = fieldDeclaration.SyntaxTree.GetCompilationUnitRoot(cancellationToken);
        var imports = compilationUnit.Usings.Any(u => u.Name?.ToString() == AbstractionsNamespace)
            || fieldDeclaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
                .Any(n => n.Usings.Any(u => u.Name?.ToString() == AbstractionsNamespace));
        var attribute = imports ? "IgnoreProperty" : $"global::{AbstractionsNamespace}.IgnoreProperty";

        var type = fieldDeclaration.Type.ToString();
        var initializer = fieldDeclaration.Type is NullableTypeSyntax ? "" : " = default!;";

        var line = text.Lines.GetLineFromPosition(fieldDeclaration.SpanStart);
        var indent = text.ToString(TextSpan.FromBounds(line.Start, fieldDeclaration.SpanStart));
        if (!string.IsNullOrWhiteSpace(indent)) indent = "";

        var newLine = IndexMapEditor.NewLine(text);
        var declaration = $"{newLine}{indent}[{attribute}] public {type} {companion} {{ get; set; }}{initializer}";

        return (document.Id, new TextChange(new TextSpan(fieldDeclaration.Span.End, 0), declaration));
    }
}
