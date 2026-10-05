using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MintPlayer.Spark.SourceGenerators.CodeFixes;

/// <summary>
/// Text edits inside a hand-written index: add a companion to every map projection, and point an
/// <c>Index(F, FieldIndexing.Search)</c> declaration at the companion instead.
/// </summary>
/// <remarks>
/// <para>
/// Edit or refuse, never guess. The supported shapes are exactly the ones measured safe in
/// docs/datetimeoffset_query_sort_filter_PRD.md, SP6 (25 fixtures, each compiled before and after):
/// a single block-bodied constructor whose <c>Map = x =&gt; …</c> / <c>AddMap&lt;T&gt;(x =&gt; …)</c>
/// bodies are a query whose final <c>select</c> (after <c>into</c>) — or a method chain whose
/// outermost call is <c>.Select(x =&gt; …)</c> — builds an anonymous or object initializer, with the
/// field exactly once as a top-level member. <c>let</c>, ternaries and implicit members are fine.
/// </para>
/// <para>
/// ⚠️ A <c>Reduce</c> is refused on purpose: a map-only edit still compiles, so the compiler would
/// not catch the map and reduce disagreeing about the result shape. So are helper projections,
/// non-<c>Select</c> terminals, block lambdas, and a companion present in only some maps.
/// </para>
/// <para>
/// Text insertion rather than node replacement: the initializer keeps its author's layout, and the new
/// member copies the indentation of the member it follows.
/// </para>
/// </remarks>
internal static class IndexMapEditor
{
    /// <summary>
    /// The insertions that add <c>{companion} = makeExpr(F's expression)</c> to every map, an empty
    /// list when every map already has it, or <see langword="null"/> when the shape is not one this
    /// can edit safely.
    /// </summary>
    public static IReadOnlyList<TextChange>? InsertCompanion(
        ClassDeclarationSyntax index, SourceText text, string field, string companion, Func<string, string> makeExpression)
    {
        var constructors = index.Members.OfType<ConstructorDeclarationSyntax>()
            .Where(c => !c.Modifiers.Any(SyntaxKind.StaticKeyword))
            .ToList();
        if (constructors.Count != 1 || constructors[0].Body is null) return null;

        var maps = new List<LambdaExpressionSyntax>();
        foreach (var statement in constructors[0].Body!.Statements.OfType<ExpressionStatementSyntax>())
        {
            switch (statement.Expression)
            {
                case AssignmentExpressionSyntax assignment when TargetName(assignment.Left) == "Reduce":
                    return null;
                case AssignmentExpressionSyntax assignment when TargetName(assignment.Left) == "Map":
                    if (assignment.Right is not LambdaExpressionSyntax map) return null;
                    maps.Add(map);
                    break;
                case InvocationExpressionSyntax invocation when TargetName(invocation.Expression) == "AddMap":
                    if (invocation.ArgumentList.Arguments.Count != 1
                        || invocation.ArgumentList.Arguments[0].Expression is not LambdaExpressionSyntax addMap)
                        return null;
                    maps.Add(addMap);
                    break;
            }
        }
        if (maps.Count == 0) return null;

        var changes = new List<TextChange>();
        var alreadyMapped = 0;
        foreach (var map in maps)
        {
            if (map.ExpressionBody is null) return null;
            if (FindProjection(map.ExpressionBody) is not { } creation) return null;

            var members = Members(creation);
            if (members.Any(m => m.Name == companion)) { alreadyMapped++; continue; }

            var hits = members.Where(m => m.Name == field).ToList();
            if (hits.Count != 1) return null;

            var hit = hits[0];
            var member = $"{companion} = {makeExpression(hit.Expression.WithoutTrivia().ToString())}";
            changes.Add(new TextChange(new TextSpan(hit.Node.Span.End, 0), Separator(text, hit.Node) + member));
        }

        // Mapped in some maps and not others: the author's intent is not readable from here.
        if (changes.Count > 0 && alreadyMapped > 0) return null;
        return changes;
    }

    /// <summary>
    /// The edit that retargets the one <c>Index(F, FieldIndexing.Search)</c> declaration to
    /// <paramref name="companion"/>, or <see langword="null"/> unless exactly one matches. Understands
    /// <c>nameof(V.F)</c>, <c>nameof(F)</c>, <c>"F"</c> and <c>x =&gt; x.F</c>.
    /// </summary>
    public static TextChange? RetargetSearchIndex(ClassDeclarationSyntax index, string field, string companion)
    {
        var changes = new List<TextChange>();
        foreach (var invocation in index.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (TargetName(invocation.Expression) != "Index" || invocation.ArgumentList.Arguments.Count != 2) continue;
            if (!invocation.ArgumentList.Arguments[1].Expression.ToString().EndsWith("FieldIndexing.Search", StringComparison.Ordinal)) continue;

            SyntaxToken? token = invocation.ArgumentList.Arguments[0].Expression switch
            {
                InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "nameof" } } nameOf
                    when nameOf.ArgumentList.Arguments.Count == 1 => LastIdentifier(nameOf.ArgumentList.Arguments[0].Expression),
                LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) => literal.Token,
                SimpleLambdaExpressionSyntax { ExpressionBody: MemberAccessExpressionSyntax member } => member.Name.Identifier,
                _ => null,
            };
            if (token is not { } t) continue;

            var isLiteral = t.IsKind(SyntaxKind.StringLiteralToken);
            if ((isLiteral ? t.ValueText : t.Text) != field) continue;

            changes.Add(new TextChange(t.Span, isLiteral ? $"\"{companion}\"" : companion));
        }

        return changes.Count == 1 ? changes[0] : null;
    }

    private static string? TargetName(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.Text,
        GenericNameSyntax generic => generic.Identifier.Text,
        MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } member => member.Name.Identifier.Text,
        _ => null,
    };

    /// <summary>The object or anonymous creation a map body projects into, or null.</summary>
    private static ExpressionSyntax? FindProjection(ExpressionSyntax body)
    {
        switch (body)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return FindProjection(parenthesized.Expression);

            case QueryExpressionSyntax query:
            {
                var queryBody = query.Body;
                while (queryBody.Continuation is not null) queryBody = queryBody.Continuation.Body;
                return queryBody.SelectOrGroup is SelectClauseSyntax select ? Creation(select.Expression) : null;
            }

            // Only the OUTERMOST call: `.Select(...).Where(...)` projects before it filters, and the
            // initializer is then not the map's result shape.
            case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member } invocation
                when member.Name.Identifier.Text == "Select":
                return invocation.ArgumentList.Arguments.Count == 1
                    && invocation.ArgumentList.Arguments[0].Expression is LambdaExpressionSyntax { ExpressionBody: { } lambdaBody }
                    ? Creation(lambdaBody)
                    : null;

            default:
                return null;
        }
    }

    private static ExpressionSyntax? Creation(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized) expression = parenthesized.Expression;

        return expression switch
        {
            AnonymousObjectCreationExpressionSyntax => expression,
            BaseObjectCreationExpressionSyntax { Initializer: { } initializer }
                when initializer.IsKind(SyntaxKind.ObjectInitializerExpression) => expression,
            _ => null,
        };
    }

    private sealed class Member(string? name, ExpressionSyntax expression, SyntaxNode node)
    {
        public string? Name { get; } = name;
        public ExpressionSyntax Expression { get; } = expression;
        public SyntaxNode Node { get; } = node;
    }

    private static List<Member> Members(ExpressionSyntax creation) => creation switch
    {
        AnonymousObjectCreationExpressionSyntax anonymous => anonymous.Initializers
            .Select(d => new Member(d.NameEquals?.Name.Identifier.Text ?? ImplicitName(d.Expression), d.Expression, d))
            .ToList(),
        BaseObjectCreationExpressionSyntax created => created.Initializer!.Expressions
            .Select(x => x is AssignmentExpressionSyntax { Left: IdentifierNameSyntax left } assignment
                ? new Member(left.Identifier.Text, assignment.Right, assignment)
                : new Member(null, x, x))
            .ToList(),
        _ => [],
    };

    /// <summary>C#'s implicit anonymous-member name: the last identifier of the expression.</summary>
    private static string? ImplicitName(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.Text,
        MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
        _ => null,
    };

    private static SyntaxToken? LastIdentifier(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier,
        MemberAccessExpressionSyntax member => member.Name.Identifier,
        _ => null,
    };

    /// <summary><c>,</c> + newline + the member's indentation when it starts its own line, else <c>, </c>.</summary>
    internal static string Separator(SourceText text, SyntaxNode node)
    {
        var line = text.Lines.GetLineFromPosition(node.SpanStart);
        var prefix = text.ToString(TextSpan.FromBounds(line.Start, node.SpanStart));
        return string.IsNullOrWhiteSpace(prefix) ? "," + NewLine(text) + prefix : ", ";
    }

    internal static string NewLine(SourceText text) => text.ToString().Contains("\r\n") ? "\r\n" : "\n";
}
