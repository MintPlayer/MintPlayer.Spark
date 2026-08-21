using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

/// <summary>
/// Reports a bare <c>MapControllers()</c> in a project that references Spark.
/// <para>
/// Controllers mapped that way sit beside Spark rather than inside it. Spark's antiforgery gate is
/// scoped to paths it was told about, and there is no way to authorize an action against the same
/// <c>security.json</c> right the Spark pipeline checks — so the app's own mutating endpoints keep
/// whatever protection it wired by hand, which in practice is none (#300). Mounting through
/// <c>spark.UseControllers()</c> puts them on the inside.
/// </para>
/// <para>
/// <b>Why a diagnostic and not a runtime check.</b> By the time <c>UseSpark()</c> executes, the app
/// has already called <c>MapControllers()</c> on its own endpoint builder, and the resulting
/// endpoints are indistinguishable from any others; nothing at
/// runtime says whether the app opted in. At compile time the call is an ordinary invocation in the
/// app's own source. SPARK004 is the existing precedent for exactly this asymmetry, in the other
/// direction.
/// </para>
/// <para>
/// The check is deliberately <b>single-file and syntactic</b>: no cross-file tracking, no
/// compilation-end analysis, no attempt to correlate with a <c>spark.UseControllers()</c> elsewhere.
/// It can afford that because <c>spark.UseControllers()</c> calls <c>MapControllers()</c> inside
/// <em>Spark's own assembly</em>, which the consuming app's compilation never analyses. So "any
/// <c>MapControllers()</c> here" is already the right predicate.
/// </para>
/// <para>
/// An app that deliberately wants the raw call suppresses the diagnostic — a reviewable act, which
/// is the entire difference from today, where the same decision is made by not knowing.
/// </para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MapControllersAnalyzer : DiagnosticAnalyzer
{
    private const string MapControllersMethodName = "MapControllers";
    private const string ControllerExtensionsFullName =
        "Microsoft.AspNetCore.Builder.ControllerEndpointRouteBuilderExtensions";

    /// <summary>Any Spark type would do; this one is the pipeline entry point every Spark app touches.</summary>
    private const string SparkMarkerTypeName = "MintPlayer.Spark.SparkExtensions";

    /// <summary>Spark's own controllers module. Its <c>UseControllers</c> is the call being recommended.</summary>
    private const string ControllersModuleTypeName = "MintPlayer.Spark.Controllers.SparkControllersExtensions";

    internal static readonly DiagnosticDescriptor BareMapControllersRule = new(
        id: "SPARK010",
        title: "MapControllers() bypasses Spark's rules",
        messageFormat: "'MapControllers()' mounts controllers outside Spark, so Spark's authorization "
            + "and antiforgery rules do not apply to them — call 'spark.UseControllers()' during "
            + "AddSpark instead, or suppress this warning to keep the bare call deliberately",
        category: "Security",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Controllers mapped with the framework's own MapControllers() are invisible to "
            + "Spark, which cannot scope its antiforgery gate to endpoints it does not know about "
            + "and cannot authorize an action against a security.json right. spark.UseControllers() "
            + "mounts the same controllers inside Spark's pipeline. This is a compile-time rule "
            + "because the call leaves no runtime trace to check.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray.Create(BareMapControllersRule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(start =>
        {
            var compilation = start.Compilation;

            // Nothing to say to a project that has never heard of Spark.
            if (compilation.GetTypeByMetadataName(SparkMarkerTypeName) is null)
                return;

            // Spark's own controllers module calls MapControllers() on purpose — that call IS the
            // recommended replacement. Recognised by the module type being DECLARED here rather than
            // referenced, so the exemption cannot be borrowed by an app that merely uses the package.
            var module = compilation.GetTypeByMetadataName(ControllersModuleTypeName);
            if (module is not null
                && SymbolEqualityComparer.Default.Equals(module.ContainingAssembly, compilation.Assembly))
            {
                return;
            }

            start.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
        });
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        if (GetInvokedName(invocation) is not { } name
            || name.Identifier.ValueText != MapControllersMethodName)
        {
            return;
        }

        if (!IsFrameworkMapControllers(context, invocation))
            return;

        context.ReportDiagnostic(Diagnostic.Create(
            BareMapControllersRule,
            name.Identifier.GetLocation()));
    }

    /// <summary>The name part of <c>x.Foo()</c> or a bare <c>Foo()</c>.</summary>
    private static SimpleNameSyntax? GetInvokedName(InvocationExpressionSyntax invocation)
        => invocation.Expression switch
        {
            MemberAccessExpressionSyntax member => member.Name,
            MemberBindingExpressionSyntax binding => binding.Name,
            IdentifierNameSyntax identifier => identifier,
            _ => null,
        };

    /// <summary>
    /// Confirms the call is ASP.NET Core's own extension when the symbol resolves.
    /// <para>
    /// An unresolved symbol is treated as a match, for the same reason SPARK004 does: while a file is
    /// being edited the semantic model is often incomplete, and an analyzer that goes quiet exactly
    /// then is the least useful kind. A symbol that resolves to something else is rejected outright,
    /// so an app's own unrelated <c>MapControllers</c> helper is left alone.
    /// </para>
    /// </summary>
    private static bool IsFrameworkMapControllers(
        SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation)
    {
        var symbol = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol
                     as IMethodSymbol;
        if (symbol is null)
            return true;

        // Reduced form for an extension invoked as a member; the declaring type is on the original.
        var declaring = (symbol.ReducedFrom ?? symbol).ContainingType?.ToDisplayString();
        return declaring == ControllerExtensionsFullName;
    }
}
