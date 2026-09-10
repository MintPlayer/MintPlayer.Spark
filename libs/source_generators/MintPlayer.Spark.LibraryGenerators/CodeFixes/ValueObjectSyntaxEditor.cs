using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Linq;

namespace MintPlayer.Spark.LibraryGenerators.CodeFixes;

/// <summary>
/// The edits SPARK016 and SPARK017 share: give a type the <c>partial</c> keyword, the
/// <c>[ValueObject]</c> attribute, and the using directive the attribute needs.
/// </summary>
/// <remarks>
/// Both fixes are written as *idempotent* transforms — each one asks whether the thing it adds is
/// already there and returns the node untouched if so. That is what lets SPARK017's fix call all
/// three unconditionally: a type that is already <c>partial</c> gets the attribute only, and no
/// caller has to know which half of the problem it is looking at.
/// </remarks>
internal static class ValueObjectSyntaxEditor
{
    private const string ValueObjectAttributeShortName = "ValueObject";
    private const string ValueObjectAttributeSuffixedName = "ValueObjectAttribute";

    internal const string AbstractionsNamespace = "MintPlayer.Spark.Abstractions";

    /// <summary>
    /// Adds <c>partial</c> unless the declaration already carries it.
    /// </summary>
    /// <remarks>
    /// ⚠️ The modifier goes *last*, immediately before the <c>class</c>/<c>record</c>/<c>struct</c>
    /// keyword, because that is the only position the language allows — <c>partial public class</c>
    /// is a compile error (CS0267 wants it adjacent to the type keyword). Appending also keeps the
    /// author's own modifier order untouched.
    /// <para>
    /// The leading trivia has to move with the first token. If <c>public</c> carried the doc comment
    /// and indentation and we simply appended after it, nothing would visibly change; but when the
    /// declaration has *no* modifiers at all, the trivia sits on the type keyword and must be
    /// transplanted onto the new <c>partial</c> token, or the type loses its comment and indent.
    /// </para>
    /// </remarks>
    public static TypeDeclarationSyntax WithPartial(TypeDeclarationSyntax declaration)
    {
        if (declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
            return declaration;

        var partial = SyntaxFactory.Token(SyntaxKind.PartialKeyword);

        if (declaration.Modifiers.Count > 0)
            return declaration.WithModifiers(
                declaration.Modifiers.Add(partial.WithTrailingTrivia(SyntaxFactory.Space)));

        // No modifiers: the type keyword owns the leading trivia, so hand it to `partial` and leave
        // the keyword with nothing but its own space.
        var keyword = declaration.Keyword;
        return declaration
            .WithModifiers(SyntaxFactory.TokenList(
                partial
                    .WithLeadingTrivia(keyword.LeadingTrivia)
                    .WithTrailingTrivia(SyntaxFactory.Space)))
            .WithKeyword(keyword.WithLeadingTrivia());
    }

    /// <summary>
    /// Adds <c>[ValueObject]</c> unless the declaration already carries it, under either spelling.
    /// </summary>
    /// <remarks>
    /// Matching on the *syntax* rather than the bound symbol is deliberate: a code fix runs on a
    /// document whose semantic model may already be stale relative to the user's keystrokes, and the
    /// only question here is whether the text would end up with the attribute twice. Both
    /// <c>[ValueObject]</c> and <c>[ValueObjectAttribute]</c> count, as does a qualified spelling,
    /// which is why the comparison is on the name's last segment.
    /// </remarks>
    public static TypeDeclarationSyntax WithValueObjectAttribute(TypeDeclarationSyntax declaration)
    {
        if (declaration.AttributeLists.SelectMany(list => list.Attributes).Any(IsValueObjectAttribute))
            return declaration;

        var attribute = SyntaxFactory.AttributeList(
            SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Attribute(SyntaxFactory.IdentifierName(ValueObjectAttributeShortName))));

        // The attribute list becomes the first thing in the declaration, so it inherits the leading
        // trivia (doc comment, indentation) and the declaration keeps a newline + indent of its own.
        var first = declaration.GetFirstToken();
        var leading = first.LeadingTrivia;

        return declaration
            .ReplaceToken(first, first.WithLeadingTrivia(IndentOnly(leading)))
            .WithAttributeLists(declaration.AttributeLists.Insert(0, attribute.WithLeadingTrivia(leading)));
    }

    /// <summary>
    /// Adds <c>using MintPlayer.Spark.Abstractions;</c> when the file does not already have it,
    /// whether through its own using list or an enclosing file-scoped/block namespace.
    /// </summary>
    /// <remarks>
    /// Returns the root unchanged when the attribute was written with a qualified name, or when a
    /// <c>global using</c> elsewhere in the project already covers it — in the latter case the
    /// duplicate would be harmless but noisy, and it is cheap to look.
    /// </remarks>
    public static CompilationUnitSyntax WithAbstractionsUsing(CompilationUnitSyntax root)
    {
        if (HasAbstractionsUsing(root.Usings) ||
            root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().Any(ns => HasAbstractionsUsing(ns.Usings)))
        {
            return root;
        }

        var directive = SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(AbstractionsNamespace))
            .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);

        // Appended rather than sorted into place: this repo's files are not consistently sorted, and
        // a fix that reorders a developer's usings produces a diff they did not ask for.
        return root.Usings.Count > 0
            ? root.WithUsings(root.Usings.Add(directive))
            : root.WithUsings(SyntaxFactory.SingletonList(directive));
    }

    private static bool HasAbstractionsUsing(SyntaxList<UsingDirectiveSyntax> usings)
        => usings.Any(u => u.Alias is null
                        && u.StaticKeyword.IsKind(SyntaxKind.None)
                        && u.Name?.ToString() == AbstractionsNamespace);

    private static bool IsValueObjectAttribute(AttributeSyntax attribute)
    {
        var name = attribute.Name switch
        {
            QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
            SimpleNameSyntax simple => simple.Identifier.Text,
            _ => attribute.Name.ToString(),
        };

        return name == ValueObjectAttributeShortName || name == ValueObjectAttributeSuffixedName;
    }

    /// <summary>
    /// The whitespace of the last line of <paramref name="trivia"/> — i.e. the declaration's indent,
    /// without any doc comment or blank lines that preceded it. Those stay with the attribute list,
    /// which is now the first thing on the declaration.
    /// </summary>
    private static SyntaxTriviaList IndentOnly(SyntaxTriviaList trivia)
    {
        // Hand-rolled rather than SyntaxTriviaList.LastIndexOf(SyntaxKind): that overload does not
        // exist, and the extension method the compiler offers instead binds to MemoryExtensions and
        // fails with a confusing CS1929 about Span<SyntaxKind>.
        var lastNewLine = -1;
        for (var i = 0; i < trivia.Count; i++)
        {
            if (trivia[i].IsKind(SyntaxKind.EndOfLineTrivia))
                lastNewLine = i;
        }

        return lastNewLine < 0
            ? SyntaxFactory.TriviaList(trivia.Where(t => t.IsKind(SyntaxKind.WhitespaceTrivia)))
            : SyntaxFactory.TriviaList(trivia.Skip(lastNewLine + 1));
    }
}
