using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Linq;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

/// <summary>
/// SPARK022: <c>!x.IsDeleted</c> or <c>x.IsDeleted == false</c> on an <c>ISoftDeletable</c> inside an
/// expression RavenDB translates.
/// </summary>
/// <remarks>
/// ⚠️ A document stored before its type became soft-deletable has no <c>IsDeleted</c> field. RavenDB
/// matches an absent field with neither <c>IsDeleted == false</c> nor <c>!IsDeleted</c>, so the filter
/// silently drops every such document — while <c>IsDeleted != true</c> keeps them. Measured on 7.2
/// (#460 spike S3: 10 rows instead of 14, on Corax and Lucene, collection and static index). In
/// memory both shapes agree, which is why the bug survives tests over freshly stored data.
/// <para>
/// Scoped to lambdas converted to <c>Expression&lt;…&gt;</c> — what a provider translates. The same
/// test in ordinary C# over a loaded entity is correct and untouched.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SoftDeleteFilterAnalyzer : DiagnosticAnalyzer
{
    private const string SoftDeletableMetadataName = "MintPlayer.Spark.SoftDelete.ISoftDeletable";

    internal static readonly DiagnosticDescriptor AbsentFieldDropsRowsRule = new(
        id: "SPARK022",
        title: "This soft-delete test drops every document without an IsDeleted field",
        messageFormat: "'{0}' does not match documents stored without an IsDeleted field, so a translated query drops them — use '{1}.IsDeleted != true'",
        category: "Correctness",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "RavenDB matches an absent field with neither '== false' nor '!x'. Documents written before the type implemented ISoftDeletable have no IsDeleted field and vanish from the query. 'IsDeleted != true' matches them.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [AbsentFieldDropsRowsRule];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

        context.RegisterCompilationStartAction(start =>
        {
            var softDeletable = start.Compilation.GetTypeByMetadataName(SoftDeletableMetadataName);
            if (softDeletable is null)
                return; // The package is not referenced: nothing can be ISoftDeletable.

            start.RegisterSyntaxNodeAction(c => AnalyzeNot(c, softDeletable), SyntaxKind.LogicalNotExpression);
            start.RegisterSyntaxNodeAction(c => AnalyzeEquals(c, softDeletable), SyntaxKind.EqualsExpression);
        });
    }

    private static void AnalyzeNot(SyntaxNodeAnalysisContext context, INamedTypeSymbol softDeletable)
    {
        var not = (PrefixUnaryExpressionSyntax)context.Node;
        if (StripParentheses(not.Operand) is not MemberAccessExpressionSyntax access) return;
        if (!IsSoftDeleteFlag(access, context, softDeletable)) return;
        if (!IsInsideTranslatedLambda(not, context)) return;

        context.ReportDiagnostic(Diagnostic.Create(AbsentFieldDropsRowsRule, not.GetLocation(), not.ToString(), access.Expression.ToString()));
    }

    private static void AnalyzeEquals(SyntaxNodeAnalysisContext context, INamedTypeSymbol softDeletable)
    {
        var equals = (BinaryExpressionSyntax)context.Node;
        MemberAccessExpressionSyntax? access = null;
        if (IsFalseLiteral(equals.Right)) access = StripParentheses(equals.Left) as MemberAccessExpressionSyntax;
        else if (IsFalseLiteral(equals.Left)) access = StripParentheses(equals.Right) as MemberAccessExpressionSyntax;
        if (access is null) return;
        if (!IsSoftDeleteFlag(access, context, softDeletable)) return;
        if (!IsInsideTranslatedLambda(equals, context)) return;

        context.ReportDiagnostic(Diagnostic.Create(AbsentFieldDropsRowsRule, equals.GetLocation(), equals.ToString(), access.Expression.ToString()));
    }

    private static ExpressionSyntax StripParentheses(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;
        return expression;
    }

    private static bool IsFalseLiteral(ExpressionSyntax expression)
        => StripParentheses(expression).IsKind(SyntaxKind.FalseLiteralExpression);

    /// <summary><c>IsDeleted</c> of <c>ISoftDeletable</c> itself or of a type implementing it.</summary>
    private static bool IsSoftDeleteFlag(MemberAccessExpressionSyntax access, SyntaxNodeAnalysisContext context, INamedTypeSymbol softDeletable)
    {
        if (access.Name.Identifier.Text != "IsDeleted") return false;
        if (context.SemanticModel.GetSymbolInfo(access, context.CancellationToken).Symbol is not IPropertySymbol property) return false;

        var owner = property.ContainingType;
        return SymbolEqualityComparer.Default.Equals(owner, softDeletable)
               || owner.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, softDeletable));
    }

    private static bool IsInsideTranslatedLambda(SyntaxNode node, SyntaxNodeAnalysisContext context)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if (current is LambdaExpressionSyntax lambda)
            {
                var converted = context.SemanticModel.GetTypeInfo(lambda, context.CancellationToken).ConvertedType;
                return converted?.OriginalDefinition.ToDisplayString() == "System.Linq.Expressions.Expression<TDelegate>";
            }

            if (current is MemberDeclarationSyntax)
                return false;
        }

        return false;
    }
}
