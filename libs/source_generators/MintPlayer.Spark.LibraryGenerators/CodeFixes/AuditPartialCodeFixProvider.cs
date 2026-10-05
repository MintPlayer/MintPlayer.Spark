using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MintPlayer.Spark.LibraryGenerators.CodeFixes;

/// <summary>
/// SPARK038 — a type implementing a framework-stamped interface (<c>IAuditable</c>, <c>ISoftDeletable</c>,
/// <c>IModeratable</c>, …) that is not <c>partial</c>. Adds the keyword to the type and to every type
/// containing it, since the generator reopens all of them.
/// </summary>
/// <remarks>
/// The diagnostic is reported from the generator of the compilation that owns the syntax, so the location
/// is always in the document being edited (as SPARK016's).
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AuditPartialCodeFixProvider)), Shared]
public sealed class AuditPartialCodeFixProvider : CodeFixProvider
{
    private const string DiagnosticId = "SPARK038";
    private const string Title = "Declare the type 'partial' so its members are generated";

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

            context.RegisterCodeFix(
                CodeAction.Create(
                    title: Title,
                    createChangedDocument: ct => AddPartialAsync(context.Document, declaration, ct),
                    equivalenceKey: nameof(AuditPartialCodeFixProvider)),
                diagnostic);
        }
    }

    private static async Task<Document> AddPartialAsync(
        Document document, TypeDeclarationSyntax declaration, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
            return document;

        var targets = declaration.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().ToList();
        return document.WithSyntaxRoot(
            root.ReplaceNodes(targets, static (_, rewritten) => ValueObjectSyntaxEditor.WithPartial(rewritten)));
    }
}
