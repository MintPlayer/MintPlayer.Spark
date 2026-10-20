using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MintPlayer.Spark.LibraryGenerators.CodeFixes;

/// <summary>
/// SPARK065 — a <c>[Replicated]</c> class that is not <c>partial</c>. Adds the keyword to the class and to
/// every class containing it, since the generator reopens all of them.
/// </summary>
/// <remarks>
/// SPARK065 also reports a generic replica, which no keyword fixes: the fix is offered only when some
/// declaration on the way up actually lacks <c>partial</c>.
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ReplicatedPartialCodeFixProvider)), Shared]
public sealed class ReplicatedPartialCodeFixProvider : CodeFixProvider
{
    private const string DiagnosticId = "SPARK065";
    private const string Title = "Declare the replica 'partial' so its replication script is generated";

    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create(DiagnosticId);

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null)
            return;

        foreach (var diagnostic in context.Diagnostics)
        {
            if (ValueObjectPartialCodeFixProvider.FindDeclaration(root, diagnostic) is not { } declaration)
                continue;

            if (Targets(declaration).All(static d => d.Modifiers.Any(SyntaxKind.PartialKeyword)))
                continue;

            context.RegisterCodeFix(
                CodeAction.Create(
                    title: Title,
                    createChangedDocument: ct => AddPartialAsync(context.Document, declaration, ct),
                    equivalenceKey: nameof(ReplicatedPartialCodeFixProvider)),
                diagnostic);
        }
    }

    private static TypeDeclarationSyntax[] Targets(TypeDeclarationSyntax declaration)
        => declaration.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().ToArray();

    private static async Task<Document> AddPartialAsync(
        Document document, TypeDeclarationSyntax declaration, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
            return document;

        return document.WithSyntaxRoot(
            root.ReplaceNodes(Targets(declaration), static (_, rewritten) => ValueObjectSyntaxEditor.WithPartial(rewritten)));
    }
}
