using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Immutable;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;

namespace MintPlayer.Spark.LibraryGenerators.CodeFixes;

/// <summary>
/// SPARK017 — a type the model reaches as a row of an embedded collection, with no
/// <c>[ValueObject]</c>. Adds the attribute, the <c>partial</c> keyword and the using, in one edit.
/// </summary>
/// <remarks>
/// ⚠️ <b>All three, or the fix is a regression.</b> <c>ValueObjectCompletenessAnalyzer</c>
/// deliberately does not check <c>partial</c> — that belongs to SPARK016, in the compilation that
/// owns the syntax. So a fix that added only the attribute would clear SPARK017 and raise SPARK016
/// on the very next build, leaving the author worse off than before: they now believe the tool
/// handled it. The three edits are idempotent (see <see cref="ValueObjectSyntaxEditor"/>), so a type
/// that is already <c>partial</c> simply gets the attribute.
/// <para>
/// <b>Why this is solution-scoped.</b> SPARK017 is computed in the *application's* compilation,
/// walking out from <c>SparkContext</c>, while the offending type usually lives in an entity
/// library — and a diagnostic cannot be reported at a location the analyzed compilation does not
/// own without the analyzer driver discarding it (measured; see
/// <c>ValueObjectCompletenessAnalyzer.LocationFor</c>). So for a cross-project offender the
/// diagnostic lands on the <c>SparkContext</c> property that reaches it, and the document handed to
/// <see cref="RegisterCodeFixesAsync"/> is the *context's* file, not the one to edit.
/// </para>
/// <para>
/// The type to change therefore has to be looked up rather than inferred from the location, which
/// is what <c>ValueObjectCompletenessAnalyzer.OffendingTypeProperty</c> carries. The edit then goes
/// through <c>createChangedSolution</c>, reaching the declaration in whichever project owns it.
/// Resolution is by <b>symbol</b> — <c>GetTypeByMetadataName</c> then
/// <c>DeclaringSyntaxReferences</c> — never by comparing file-path strings across
/// <c>solution.Projects.SelectMany(p =&gt; p.Documents)</c>, which is how the equivalent fix in
/// MintPlayer.Dotnet.Tools does it and how it once silently matched nothing at all.
/// </para>
/// <para>
/// ⚠️ What genuinely stays out of reach: a type that arrives as a true <b>metadata</b> reference (a
/// NuGet-packaged entity library, or any <c>dotnet build</c>, where a referenced project is a .dll).
/// There the diagnostic has no source location and there is no document to edit. The error is still
/// raised — severity is the enforcement — but no fix is offered, and none can be.
/// </para>
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ValueObjectCompletenessCodeFixProvider)), Shared]
public sealed class ValueObjectCompletenessCodeFixProvider : CodeFixProvider
{
    private const string DiagnosticId = "SPARK017";
    private const string Title = "Make this a value object";

    /// <summary>
    /// Mirrors <c>ValueObjectCompletenessAnalyzer.OffendingTypeProperty</c>. Duplicated as a
    /// literal rather than referenced: the analyzer lives in MintPlayer.Spark.SourceGenerators and
    /// this fix in MintPlayer.Spark.LibraryGenerators, and neither Roslyn component may reference
    /// the other. The pairing is covered by <c>ValueObjectCodeFixTests</c>, which runs the real
    /// analyzer and the real fix together — so a rename that breaks the contract fails a test
    /// rather than silently producing a diagnostic with no lightbulb.
    /// </summary>
    private const string OffendingTypeProperty = "SparkOffendingType";

    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create(DiagnosticId);

    // No "Fix all" — see the note in ValueObjectPartialCodeFixProvider. It would be worth more here
    // than there, because one bad model can flag a whole subtree, but WellKnownFixAllProvider comes
    // from Microsoft.CodeAnalysis.Features and that dependency is not worth taking on speculatively.

    public override Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        foreach (var diagnostic in context.Diagnostics)
        {
            if (!diagnostic.Properties.TryGetValue(OffendingTypeProperty, out var metadataName)
                || string.IsNullOrEmpty(metadataName))
            {
                continue;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    title: Title,
                    createChangedSolution: ct => MakeValueObjectAsync(
                        context.Document.Project.Solution, metadataName!, ct),
                    equivalenceKey: nameof(ValueObjectCompletenessCodeFixProvider)),
                diagnostic);
        }

        return Task.CompletedTask;
    }

    private static async Task<Solution> MakeValueObjectAsync(
        Solution solution, string metadataName, CancellationToken cancellationToken)
    {
        var found = await FindDeclarationAsync(solution, metadataName, cancellationToken).ConfigureAwait(false);
        if (found is not { } hit)
            return solution;

        var (document, declaration) = hit;

        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
            return solution;

        var updated = ValueObjectSyntaxEditor.WithValueObjectAttribute(
            ValueObjectSyntaxEditor.WithPartial(declaration));

        var newRoot = root.ReplaceNode(declaration, updated);

        // The using goes on last, and only if the file has one to gain: the attribute may have been
        // written qualified, or a global using may already cover it.
        if (newRoot is CompilationUnitSyntax compilationUnit)
            newRoot = ValueObjectSyntaxEditor.WithAbstractionsUsing(compilationUnit);

        return solution.WithDocumentSyntaxRoot(document.Id, newRoot);
    }

    /// <summary>
    /// The declaration of <paramref name="metadataName"/> in whichever project of the solution
    /// declares it in source, or <see langword="null"/> when no project does.
    /// </summary>
    /// <remarks>
    /// Searched per project because the type is generally not in the project the diagnostic came
    /// from. A type declared in more than one file (already <c>partial</c>) is edited in its first
    /// declaration, which is where the attribute belongs — repeating it on the other halves would
    /// not compile.
    /// <para>
    /// Returns nothing for a type that exists only as metadata: a NuGet-packaged entity library has
    /// no document to edit, and no fix is possible. The diagnostic still stands.
    /// </para>
    /// </remarks>
    private static async Task<(Document Document, TypeDeclarationSyntax Declaration)?> FindDeclarationAsync(
        Solution solution, string metadataName, CancellationToken cancellationToken)
    {
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation?.Assembly is null)
                continue;

            // Only the project that actually declares the type, not every project that can see it:
            // GetTypeByMetadataName resolves through references too, and editing the reference's
            // copy of the symbol would find no document.
            var symbol = compilation.Assembly.GetTypeByMetadataName(metadataName);
            if (symbol is null)
                continue;

            foreach (var reference in symbol.DeclaringSyntaxReferences)
            {
                var node = await reference.GetSyntaxAsync(cancellationToken).ConfigureAwait(false);
                if (node is not TypeDeclarationSyntax declaration)
                    continue;

                if (project.GetDocument(reference.SyntaxTree) is { } document)
                    return (document, declaration);
            }
        }

        return null;
    }
}
