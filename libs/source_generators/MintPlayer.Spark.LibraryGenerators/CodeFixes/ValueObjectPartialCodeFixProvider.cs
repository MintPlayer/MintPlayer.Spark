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
/// SPARK016 — a type decorated <c>[ValueObject]</c> that is not <c>partial</c> and has no
/// <c>[ValueKey]</c>. Adds the keyword.
/// </summary>
/// <remarks>
/// The simplest fix in the framework, and deliberately so: SPARK016 is reported by
/// <c>ValueObjectKeyReporter</c> from inside the generator pipeline of the compilation that *owns
/// the syntax* — the entity library — so the location is always real source in the document being
/// edited. No solution-wide search, no cross-project write.
/// <para>
/// ⚠️ It fixes only the <c>partial</c> half. The author's other legitimate answer is to mark an
/// existing property <c>[ValueKey]</c>, which the diagnostic message offers, but that requires
/// choosing *which* property and no fix can guess it. Offering a second action that picks one for
/// them would be worse than offering none.
/// </para>
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ValueObjectPartialCodeFixProvider)), Shared]
public sealed class ValueObjectPartialCodeFixProvider : CodeFixProvider
{
    private const string DiagnosticId = "SPARK016";
    private const string Title = "Declare the value object 'partial'";

    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create(DiagnosticId);

    // No GetFixAllProvider override, so the base returns null and the IDE offers no "Fix all".
    // WellKnownFixAllProvider.BatchFixer would be the natural choice but lives in
    // Microsoft.CodeAnalysis.Features, a much heavier reference than the Workspaces one the fix
    // itself needs, for a diagnostic that fires one type at a time. SPARK017 is the one that can
    // flag a whole subtree at once; revisit there first if Fix All is ever wanted.

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null)
            return;

        foreach (var diagnostic in context.Diagnostics)
        {
            if (FindDeclaration(root, diagnostic) is not { } declaration)
                continue;

            context.RegisterCodeFix(
                CodeAction.Create(
                    title: Title,
                    createChangedDocument: ct => AddPartialAsync(context.Document, declaration, ct),
                    equivalenceKey: nameof(ValueObjectPartialCodeFixProvider)),
                diagnostic);
        }
    }

    private static async Task<Document> AddPartialAsync(
        Document document, TypeDeclarationSyntax declaration, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
            return document;

        return document.WithSyntaxRoot(
            root.ReplaceNode(declaration, ValueObjectSyntaxEditor.WithPartial(declaration)));
    }

    /// <summary>
    /// The type declaration the diagnostic points at, or <see langword="null"/> when the span does
    /// not resolve to one.
    /// </summary>
    /// <remarks>
    /// A diagnostic with <see cref="Location.None"/> never reaches a fix provider — the IDE has no
    /// document to attach it to — so the null return here covers only a stale span, i.e. the user
    /// edited the file between the diagnostic being computed and the lightbulb being opened.
    /// </remarks>
    internal static TypeDeclarationSyntax? FindDeclaration(SyntaxNode root, Diagnostic diagnostic)
    {
        var span = diagnostic.Location.SourceSpan;
        if (span.End > root.FullSpan.End)
            return null;

        return root.FindToken(span.Start).Parent?
            .AncestorsAndSelf()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault();
    }
}
