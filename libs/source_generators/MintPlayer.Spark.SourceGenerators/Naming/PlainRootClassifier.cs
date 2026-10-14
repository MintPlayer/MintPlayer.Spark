using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MintPlayer.Spark.SourceGenerators.Models;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace MintPlayer.Spark.SourceGenerators.Naming;

/// <summary>
/// Which roots of a <c>SparkContext</c> are <b>plain</b> entity roots (<c>docs/default_index_PRD.md</c>, D6; PRD §7,
/// SP3): the ones a query-less <c>[Reference(typeof(T))]</c> resolves to. One classifier for the generator, which
/// records them as <c>[SparkPlainRoot]</c> on the context's generated partial, and for SPARK056, which warns when a
/// referenced type has none — written twice, the two would disagree about a reference the first time either changed.
/// </summary>
internal static class PlainRootClassifier
{
    internal const string RavenQueryableMetadataName = "Raven.Client.Documents.Linq.IRavenQueryable`1";
    private const string RavenSessionNamespace = "Raven.Client.Documents.Session";

    /// <summary>
    /// Every plain root of <paramref name="contextType"/>, own or inherited from a base in this compilation: a public
    /// instance <c>IRavenQueryable&lt;T&gt;</c> property over a non-view <c>T</c>, not hidden by a derived member of the
    /// same name, whose body <see cref="IsPlainRoot">is plain</see>.
    /// </summary>
    /// <remarks>
    /// A base context from a referenced assembly has no syntax here, so its roots classify as not plain; its markers
    /// come from its own generator run (D6), and the callers read those separately.
    /// </remarks>
    internal static IEnumerable<(IPropertySymbol Property, ITypeSymbol Entity)> PlainRoots(
        INamedTypeSymbol contextType, INamedTypeSymbol ravenQueryable, Compilation compilation, CancellationToken ct)
    {
        var seen = new HashSet<string>(System.StringComparer.Ordinal);
        for (var current = contextType; current is not null; current = current.BaseType)
        {
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
            {
                ct.ThrowIfCancellationRequested();

                // The synchronizer reads public roots only, and a root hidden by a derived one is not the root.
                if (property.IsStatic || property.DeclaredAccessibility != Accessibility.Public) continue;
                if (property.Type is not INamedTypeSymbol { TypeArguments.Length: 1 } queryable
                    || !SymbolEqualityComparer.Default.Equals(queryable.OriginalDefinition, ravenQueryable))
                    continue;
                if (!seen.Add(property.Name)) continue;

                var exposed = queryable.TypeArguments[0];
                if (exposed is INamedTypeSymbol named && named.HasFromIndex()) continue;

                if (IsPlainRoot(property, exposed, compilation, ct))
                    yield return (property, exposed);
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="property"/> is the plain entity root of <paramref name="entity"/> (PRD §7, SP3): its body,
    /// in any of the three forms (<c>=&gt; e</c>, <c>get =&gt; e</c>, <c>get { return e; }</c>), is a single
    /// <c>Query&lt;T&gt;()</c> call with no arguments on a RavenDB session (<c>Session.Query&lt;T&gt;()</c>,
    /// <c>this.Session.Query&lt;T&gt;()</c>).
    /// </summary>
    /// <remarks>
    /// Conservative by design: a filter, an index or collection argument, <c>.Customize(...)</c>, a local, or a helper
    /// method (even one that only wraps <c>Session.Query&lt;T&gt;()</c>) is "not plain". A reference to such a type then
    /// has to name its query, which is a visible, fixable answer; a wrong "plain" would silently route lookups through
    /// a filtered root.
    /// </remarks>
    internal static bool IsPlainRoot(IPropertySymbol property, ITypeSymbol entity, Compilation compilation, CancellationToken ct)
    {
        var declaration = property.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax(ct))
            .OfType<PropertyDeclarationSyntax>()
            .FirstOrDefault();
        if (declaration is null) return false;

        var body = declaration.ExpressionBody?.Expression;
        if (body is null)
        {
            var getter = declaration.AccessorList?.Accessors
                .FirstOrDefault(a => a.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.GetAccessorDeclaration));
            body = getter?.ExpressionBody?.Expression
                ?? (getter?.Body?.Statements is { Count: 1 } statements
                    && statements[0] is ReturnStatementSyntax { Expression: { } returned }
                        ? returned
                        : null);
        }

        while (body is ParenthesizedExpressionSyntax parenthesized)
            body = parenthesized.Expression;

        if (body is not InvocationExpressionSyntax invocation) return false;
        if (invocation.ArgumentList.Arguments.Count > 0) return false;

        // The syntax tree must belong to this compilation: a property from a referenced compilation's source (a
        // project reference read through its syntax) has no semantic model here.
        if (!compilation.ContainsSyntaxTree(declaration.SyntaxTree)) return false;
        var model = compilation.GetSemanticModel(declaration.SyntaxTree);
        if (model.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method) return false;

        return method.Name == "Query"
            && method.TypeArguments.Length == 1
            && SymbolEqualityComparer.Default.Equals(method.TypeArguments[0], entity)
            && method.ContainingType?.ContainingNamespace?.ToDisplayString() == RavenSessionNamespace;
    }
}
