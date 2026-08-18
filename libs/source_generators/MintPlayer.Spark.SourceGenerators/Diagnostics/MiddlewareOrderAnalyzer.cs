using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

/// <summary>
/// Reports <c>app.UseSpark()</c> called before <c>app.UseRouting()</c>.
/// <para>
/// <c>UseSpark</c> adds middleware that reads endpoint metadata — the rate limiter's
/// <c>[EnableRateLimiting]</c> / <c>[DisableRateLimiting]</c>, and <c>UseAuthorization</c>'s
/// <c>[Authorize]</c>. Before routing has run no endpoint is selected, so that metadata is invisible
/// and silently stops applying: authorization is not evaluated per endpoint, and rate limiting falls
/// back to the global budget. Nothing throws, and nothing is logged.
/// </para>
/// <para>
/// Ordering is a property of the code rather than of a request, which is why this is a compile-time
/// diagnostic and not a runtime check. It follows ASP.NET Core's own precedent: <c>UseRouting</c>,
/// <c>UseAuthentication</c>, <c>UseAuthorization</c> and <c>UseEndpoints</c> perform no runtime order
/// checks, and the equivalent rule ships as analyzer <c>ASP0001</c> ("The call to UseAuthorization
/// should appear between app.UseRouting() and app.UseEndpoints(..) for authorization to be correctly
/// evaluated").
/// </para>
/// <para>
/// Deliberately conservative — it reports only when both calls are visible in the same body and their
/// order is provably wrong:
/// <list type="bullet">
/// <item><description>
/// No <c>UseRouting()</c> in the body at all → <b>no diagnostic.</b> That is the correct
/// minimal-hosting shape: <c>WebApplication</c> inserts routing at the front of the pipeline itself.
/// It also covers routing configured in a helper method this analyzer cannot see.
/// </description></item>
/// <item><description>
/// Position is taken from the invoked method's <em>name</em>, not the invocation node, so a chained
/// <c>app.UseSpark().UseRouting()</c> is ordered the same way the runtime orders it — left to right.
/// </description></item>
/// </list>
/// </para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MiddlewareOrderAnalyzer : DiagnosticAnalyzer
{
    private const string SparkExtensionsFullName = "MintPlayer.Spark.SparkExtensions";
    private const string AspNetCoreBuilderNamespace = "Microsoft.AspNetCore.Builder";

    private static readonly HashSet<string> SparkPipelineMethods = new() { "UseSpark", "UseSparkFull" };

    internal static readonly DiagnosticDescriptor UseSparkBeforeUseRoutingRule = new(
        id: "SPARK004",
        title: "UseSpark() should be called after UseRouting()",
        messageFormat: "'{0}()' is called before 'UseRouting()'. Endpoint metadata such as [Authorize], "
            + "[EnableRateLimiting] and [DisableRateLimiting] is silently ignored by middleware that runs "
            + "before routing — move 'app.UseRouting()' above 'app.{0}()'",
        category: "Correctness",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "UseSpark adds middleware that reads endpoint metadata, including authorization and "
            + "rate-limiting policies. Before routing has run no endpoint is selected, so that metadata "
            + "does not apply and the failure is silent. Call app.UseRouting() first.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray.Create(UseSparkBeforeUseRoutingRule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        if (GetInvokedName(invocation) is not { } name || !SparkPipelineMethods.Contains(name.Identifier.ValueText))
            return;

        if (!IsSparkPipelineCall(context, invocation))
            return;

        // The body the call lives in. For top-level statements this is the compilation unit, which is
        // what makes a minimal-hosting Program.cs work without special handling.
        var scope = GetEnclosingScope(invocation);
        if (scope is null)
            return;

        var routingPosition = FindFirstUseRoutingNamePosition(context, scope);
        if (routingPosition is null)
            return; // Routing not visible here — say nothing rather than guess.

        if (name.Identifier.SpanStart < routingPosition.Value)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                UseSparkBeforeUseRoutingRule,
                name.Identifier.GetLocation(),
                name.Identifier.ValueText));
        }
    }

    /// <summary>
    /// The name part of <c>x.Foo()</c> or a bare <c>Foo()</c>, or <see langword="null"/> for anything
    /// else (an invoked delegate, an indexer, and so on).
    /// </summary>
    private static SimpleNameSyntax? GetInvokedName(InvocationExpressionSyntax invocation)
        => invocation.Expression switch
        {
            MemberAccessExpressionSyntax member => member.Name,
            MemberBindingExpressionSyntax binding => binding.Name,
            IdentifierNameSyntax identifier => identifier,
            _ => null,
        };

    /// <summary>
    /// Confirms the call is Spark's own pipeline extension when the symbol resolves.
    /// <para>
    /// An unresolved symbol is treated as a match: while a file is being edited the model is often
    /// incomplete, and an analyzer that goes quiet exactly then is the least useful kind. The method
    /// names are distinctive enough that a false positive on an unrelated <c>UseSpark</c> costs a
    /// warning, whereas silence costs the diagnostic its purpose. A symbol that resolves to something
    /// else is rejected outright.
    /// </para>
    /// </summary>
    private static bool IsSparkPipelineCall(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation)
    {
        var symbol = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol
                     as IMethodSymbol;
        if (symbol is null)
            return true;

        var containing = symbol.ContainingType?.ToDisplayString();
        return containing == SparkExtensionsFullName
            || containing?.EndsWith("SparkFullExtensions", System.StringComparison.Ordinal) == true;
    }

    /// <summary>
    /// Source position of the first <c>UseRouting</c> name in <paramref name="scope"/>, or
    /// <see langword="null"/> if there is none.
    /// <para>
    /// Compares the <em>name token</em> rather than the invocation node on purpose. In a chain like
    /// <c>app.UseSpark().UseRouting()</c> every invocation node starts at <c>app</c>, so invocation
    /// spans cannot order them; the name tokens run left to right, which is also the order the runtime
    /// composes them in.
    /// </para>
    /// </summary>
    private static int? FindFirstUseRoutingNamePosition(SyntaxNodeAnalysisContext context, SyntaxNode scope)
    {
        int? earliest = null;

        foreach (var candidate in scope.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (GetInvokedName(candidate) is not { } candidateName
                || candidateName.Identifier.ValueText != "UseRouting")
            {
                continue;
            }

            var symbol = context.SemanticModel.GetSymbolInfo(candidate, context.CancellationToken).Symbol
                         as IMethodSymbol;
            if (symbol is not null
                && symbol.ContainingNamespace?.ToDisplayString() != AspNetCoreBuilderNamespace)
            {
                continue;
            }

            var position = candidateName.Identifier.SpanStart;
            if (earliest is null || position < earliest)
                earliest = position;
        }

        return earliest;
    }

    /// <summary>
    /// The nearest enclosing body: a method, local function, lambda, accessor, constructor, or — for
    /// top-level statements — the compilation unit. Lambdas count as their own scope because
    /// <c>webBuilder.Configure(app => ...)</c> puts a whole pipeline inside one.
    /// </summary>
    private static SyntaxNode? GetEnclosingScope(SyntaxNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case AnonymousFunctionExpressionSyntax:
                case LocalFunctionStatementSyntax:
                case BaseMethodDeclarationSyntax:
                case AccessorDeclarationSyntax:
                case CompilationUnitSyntax:
                    return current;
            }
        }

        return null;
    }
}
