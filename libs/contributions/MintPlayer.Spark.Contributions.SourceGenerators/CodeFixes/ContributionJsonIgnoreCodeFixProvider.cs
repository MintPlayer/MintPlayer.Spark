using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Immutable;
using System.Composition;

namespace MintPlayer.Spark.Contributions.SourceGenerators.CodeFixes;

/// <summary>
/// SPARK029 — a <c>[Contribution]</c> property without Newtonsoft's <c>[JsonIgnore]</c>. Adds
/// <c>[Newtonsoft.Json.JsonIgnore]</c>, fully qualified, so it can never bind to System.Text.Json's
/// attribute through a using (which would leave the rows stored on the target).
/// </summary>
/// <remarks>
/// The diagnostic is on the property, in the document being edited, so this is a document-local fix.
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ContributionJsonIgnoreCodeFixProvider)), Shared]
public sealed class ContributionJsonIgnoreCodeFixProvider : CodeFixProvider
{
    private const string Title = "Add [Newtonsoft.Json.JsonIgnore]";

    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create(ContributionDiagnostics.MissingJsonIgnore.Id);

    /// <summary>No "Fix all": WellKnownFixAllProviders lives in Microsoft.CodeAnalysis.Features, not worth the reference.</summary>
    public override FixAllProvider? GetFixAllProvider() => null;

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null)
            return;

        foreach (var diagnostic in context.Diagnostics)
        {
            var property = root.FindToken(diagnostic.Location.SourceSpan.Start).Parent?
                .AncestorsAndSelf().OfType<PropertyDeclarationSyntax>().FirstOrDefault();
            if (property is null)
                continue;

            context.RegisterCodeFix(
                CodeAction.Create(
                    Title,
                    ct => AddJsonIgnoreAsync(context.Document, property, ct),
                    equivalenceKey: nameof(ContributionJsonIgnoreCodeFixProvider)),
                diagnostic);
        }
    }

    private static async Task<Document> AddJsonIgnoreAsync(Document document, PropertyDeclarationSyntax property, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
            return document;

        var attribute = SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(
            SyntaxFactory.Attribute(SyntaxFactory.ParseName("Newtonsoft.Json.JsonIgnore"))));

        // The diagnostic needs [Contribution], so there is always an attribute list. The new one goes
        // after the last, on its own line, copying that list's indentation and line ending.
        var last = property.AttributeLists.Last();
        var ownLine = last.GetTrailingTrivia().Any(t => t.IsKind(SyntaxKind.EndOfLineTrivia));
        var indentation = ownLine
            ? SyntaxFactory.TriviaList(last.GetLeadingTrivia().Reverse().TakeWhile(t => t.IsKind(SyntaxKind.WhitespaceTrivia)).Reverse())
            : SyntaxFactory.TriviaList();
        var lineEnd = ownLine ? last.GetTrailingTrivia() : SyntaxFactory.TriviaList(SyntaxFactory.Space);
        var updated = property.WithAttributeLists(property.AttributeLists.Add(
            attribute.WithLeadingTrivia(indentation).WithTrailingTrivia(lineEnd)));

        return document.WithSyntaxRoot(root.ReplaceNode(property, updated));
    }
}
