using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Linq;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

/// <summary>
/// SPARK019: <c>Nullable&lt;T&gt;.HasValue</c> inside an expression RavenDB will translate.
/// </summary>
/// <remarks>
/// ⚠️ RavenDB translates <c>x.Foo.HasValue</c> as a <b>field name</b> — <c>Foo_HasValue</c> — rather
/// than as a null test. No index map emits such a field, so against a static index the whole query
/// throws <c>ArgumentException</c> ("The field 'Foo_HasValue' is not indexed in …") and the caller
/// gets an HTTP 500. Measured on 7.2.6 and pinned by <c>AbsentVersusNullFieldTests</c>.
/// <para>
/// The rule does not try to work out which index a given expression will run against — it cannot, and
/// neither can anything else at compile time, because the binding is a runtime decision read from
/// model JSON. It does not need to: <c>x.Foo != null</c> is exactly equivalent in C# and translates
/// correctly on every provider, so the replacement is safe whether or not the query would have hit a
/// static index. A rule that is right either way needs no such analysis.
/// </para>
/// <para>
/// Scoped to members whose signature says the expression is destined for a provider — a
/// <c>GetRowFilterAsync</c>-shaped <c>Expression&lt;Func&lt;T, bool&gt;&gt;</c>, or a method returning
/// a queryable. Ordinary C# using <c>HasValue</c> in memory is untouched, which is most of it.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RavenHasValueAnalyzer : DiagnosticAnalyzer
{
    internal static readonly DiagnosticDescriptor HasValueInTranslatedExpressionRule = new(
        id: "SPARK019",
        title: "HasValue is translated as a field name, not a null test",
        messageFormat: "'{0}.HasValue' is translated by RavenDB as the field '{0}_HasValue', which no index emits — use '{0} != null' instead",
        category: "Correctness",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "RavenDB turns Nullable<T>.HasValue into a field name rather than a null test. Against a static index that field does not exist, so the entire query throws and the caller sees a 500. `!= null` is equivalent in C# and translates correctly everywhere.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [HasValueInTranslatedExpressionRule];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();

        // Generated code never writes HasValue into a query expression, and a diagnostic located in a
        // generated tree would be dropped anyway.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.SimpleMemberAccessExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var access = (MemberAccessExpressionSyntax)context.Node;
        if (access.Name.Identifier.Text != "HasValue") return;

        // Only on an actual Nullable<T>; a property called HasValue on some other type is not ours.
        var symbol = context.SemanticModel.GetSymbolInfo(access, context.CancellationToken).Symbol;
        if (symbol is not IPropertySymbol { ContainingType.OriginalDefinition.SpecialType: SpecialType.System_Nullable_T })
            return;

        if (!IsInsideTranslatedExpression(access, context)) return;

        context.ReportDiagnostic(Diagnostic.Create(
            HasValueInTranslatedExpressionRule,
            access.GetLocation(),
            access.Expression.ToString()));
    }

    /// <summary>
    /// Whether this access sits in something a query provider will translate rather than execute.
    /// </summary>
    /// <remarks>
    /// Deliberately signature-based. Trying to follow the value to a specific provider would need the
    /// index binding, which lives in model JSON and is resolved at runtime — the same wall
    /// <c>SortCompanionAnalyzer</c> documents for reading a Map. Two shapes cover everything Spark
    /// hands to RavenDB: an <c>Expression&lt;Func&lt;…&gt;&gt;</c> (a row filter) and a method whose
    /// return type is a queryable (a custom query).
    /// </remarks>
    private static bool IsInsideTranslatedExpression(SyntaxNode node, SyntaxNodeAnalysisContext context)
    {
        for (var current = node; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case MethodDeclarationSyntax method:
                    return NamesTranslatedType(method.ReturnType, context);

                case PropertyDeclarationSyntax property:
                    return NamesTranslatedType(property.Type, context);

                // A lambda assigned to a local is still evaluated somewhere this cannot see; stop at
                // the member boundary rather than guessing.
                case LocalFunctionStatementSyntax local:
                    return NamesTranslatedType(local.ReturnType, context);
            }
        }

        return false;
    }

    private static bool NamesTranslatedType(TypeSyntax? type, SyntaxNodeAnalysisContext context)
    {
        if (type is null) return false;

        var symbol = context.SemanticModel.GetTypeInfo(type, context.CancellationToken).Type;
        for (var current = symbol; current is not null; current = (current as INamedTypeSymbol)?.TypeArguments.FirstOrDefault())
        {
            var name = current.OriginalDefinition.ToDisplayString();

            if (name is "System.Linq.Expressions.Expression<TDelegate>"
                or "Raven.Client.Documents.Linq.IRavenQueryable<T>"
                or "System.Linq.IQueryable<T>")
            {
                return true;
            }
        }

        return false;
    }
}
