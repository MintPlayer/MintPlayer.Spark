using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MintPlayer.Spark.SourceGenerators.Diagnostics;
using System.Collections.Immutable;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;

namespace MintPlayer.Spark.SourceGenerators.CodeFixes;

/// <summary>
/// SPARK057 / SPARK058 (D28): states the rename that resolves the collision, as an assembly attribute in
/// the document the diagnostic is reported in (the application's context):
/// <list type="bullet">
/// <item>SPARK057: <c>[assembly: MintPlayer.Spark.Abstractions.PersistentObjectName("alias", "Declared", "AliasDeclared")]</c>.</item>
/// <item>SPARK058: <c>[assembly: MintPlayer.Spark.Abstractions.PersistentObjectName&lt;global::App.Order&gt;("AppOrder")]</c>.</item>
/// </list>
/// The suggested name is a starting point the developer is expected to change. Fully qualified, so no
/// <c>using</c> is added. IDE-only: <c>dotnet build</c> never loads a fix provider.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(PersistentObjectNameCodeFixProvider)), Shared]
public sealed class PersistentObjectNameCodeFixProvider : CodeFixProvider
{
    private const string AttributeName = "MintPlayer.Spark.Abstractions.PersistentObjectName";

    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create("SPARK057", "SPARK058");

    public override FixAllProvider? GetFixAllProvider() => null;

    public override Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        foreach (var diagnostic in context.Diagnostics)
        {
            if (!diagnostic.Properties.TryGetValue(PersistentObjectNameAnalyzer.SuggestedNameProperty, out var suggested) || suggested is null) continue;

            string attribute;
            string title;
            if (diagnostic.Id == "SPARK057")
            {
                if (!diagnostic.Properties.TryGetValue(PersistentObjectNameAnalyzer.AliasProperty, out var alias) || alias is null) continue;
                if (!diagnostic.Properties.TryGetValue(PersistentObjectNameAnalyzer.DeclaredNameProperty, out var declared) || declared is null) continue;
                attribute = $"{AttributeName}({Literal(alias)}, {Literal(declared)}, {Literal(suggested)})";
                title = $"Rename '{alias}:{declared}' to '{suggested}'";
            }
            else
            {
                if (!diagnostic.Properties.TryGetValue(PersistentObjectNameAnalyzer.EntityTypeProperty, out var entity) || entity is null) continue;
                attribute = $"{AttributeName}<global::{entity.Replace('+', '.')}>({Literal(suggested)})";
                title = $"Name '{entity}' '{suggested}'";
            }

            context.RegisterCodeFix(
                CodeAction.Create(title, ct => AddAsync(context.Document, attribute, ct), equivalenceKey: $"{nameof(PersistentObjectNameCodeFixProvider)}.{diagnostic.Id}.{attribute}"),
                diagnostic);
        }

        return Task.CompletedTask;
    }

    private static async Task<Document> AddAsync(Document document, string attribute, CancellationToken cancellationToken)
    {
        if (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not CompilationUnitSyntax unit)
            return document;

        var list = SyntaxFactory.ParseCompilationUnit($"[assembly: {attribute}]\n").AttributeLists[0]
            .WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);
        return document.WithSyntaxRoot(unit.AddAttributeLists(list));
    }

    private static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);
}
