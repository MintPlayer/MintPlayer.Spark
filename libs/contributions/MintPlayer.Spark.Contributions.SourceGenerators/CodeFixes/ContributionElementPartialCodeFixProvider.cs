using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Immutable;
using System.Composition;

namespace MintPlayer.Spark.Contributions.SourceGenerators.CodeFixes;

/// <summary>
/// SPARK031 — a contribution element (or a type containing it) that is not <c>partial</c>. Adds the
/// keyword to the element and to every containing type that lacks it.
/// </summary>
/// <remarks>
/// <para>
/// The diagnostic sits on the <c>[Contribution]</c> property, while the declaration to edit is the
/// element's, generally in another file. The element is therefore resolved by <b>symbol</b> from the
/// metadata name the diagnostic carries (<see cref="ContributionDiagnostics.ElementMetadataNameProperty"/>),
/// in whichever project of the solution declares it in source, and the edit goes through
/// <c>createChangedSolution</c> — the same scheme as SPARK017's fix in LibraryGenerators.
/// </para>
/// <para>
/// SPARK031 is only raised for an element of the analyzed compilation (an element of another assembly
/// is SPARK032: a generator cannot add members across compilations, so <c>partial</c> would not help).
/// A type declared in several files gets the keyword in each declaration that lacks it.
/// </para>
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ContributionElementPartialCodeFixProvider)), Shared]
public sealed class ContributionElementPartialCodeFixProvider : CodeFixProvider
{
    private const string Title = "Declare the contribution element 'partial'";

    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create(ContributionDiagnostics.ElementNotPartial.Id);

    /// <summary>No "Fix all": WellKnownFixAllProviders lives in Microsoft.CodeAnalysis.Features, not worth the reference.</summary>
    public override FixAllProvider? GetFixAllProvider() => null;

    /// <inheritdoc />
    public override Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        foreach (var diagnostic in context.Diagnostics)
        {
            if (!diagnostic.Properties.TryGetValue(ContributionDiagnostics.ElementMetadataNameProperty, out var metadataName)
                || string.IsNullOrEmpty(metadataName))
            {
                continue;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    Title,
                    ct => MakePartialAsync(context.Document.Project.Solution, metadataName!, ct),
                    equivalenceKey: nameof(ContributionElementPartialCodeFixProvider)),
                diagnostic);
        }

        return Task.CompletedTask;
    }

    private static async Task<Solution> MakePartialAsync(Solution solution, string metadataName, CancellationToken cancellationToken)
    {
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);

            // Only the project that declares the type, not one that merely references it.
            if (compilation?.Assembly.GetTypeByMetadataName(metadataName) is not { } element)
                continue;

            // Every declaration of the element and of its containing types, grouped per document.
            var declarations = new List<SyntaxReference>();
            for (var t = element; t is not null; t = t.ContainingType)
                declarations.AddRange(t.DeclaringSyntaxReferences);

            foreach (var group in declarations.GroupBy(r => r.SyntaxTree))
            {
                var document = solution.GetDocument(group.Key);
                if (document is null)
                    continue;

                var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                if (root is null)
                    continue;

                var nodes = new List<TypeDeclarationSyntax>();
                foreach (var reference in group)
                {
                    if (await reference.GetSyntaxAsync(cancellationToken).ConfigureAwait(false) is TypeDeclarationSyntax node)
                        nodes.Add(node);
                }

                var newRoot = root.ReplaceNodes(nodes, static (_, rewritten) => WithPartial(rewritten));
                solution = solution.WithDocumentSyntaxRoot(document.Id, newRoot);
            }

            return solution;
        }

        return solution;
    }

    /// <summary>
    /// Adds <c>partial</c> right before the type keyword (the only position the language allows),
    /// moving the leading trivia onto it when the declaration has no modifiers.
    /// </summary>
    private static TypeDeclarationSyntax WithPartial(TypeDeclarationSyntax declaration)
    {
        if (declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
            return declaration;

        var partial = SyntaxFactory.Token(SyntaxKind.PartialKeyword);
        if (declaration.Modifiers.Count > 0)
            return declaration.WithModifiers(declaration.Modifiers.Add(partial.WithTrailingTrivia(SyntaxFactory.Space)));

        var keyword = declaration.Keyword;
        return declaration
            .WithModifiers(SyntaxFactory.TokenList(partial.WithLeadingTrivia(keyword.LeadingTrivia).WithTrailingTrivia(SyntaxFactory.Space)))
            .WithKeyword(keyword.WithLeadingTrivia());
    }
}
