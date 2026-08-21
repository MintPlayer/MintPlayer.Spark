using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using MintPlayer.SourceGenerators.Tools;
using System.Collections.Immutable;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

/// <summary>
/// Reports a bare <c>MapControllers()</c> in a project that references Spark.
/// <para>
/// Controllers mapped that way sit beside Spark rather than inside it: its antiforgery gate is
/// scoped to paths it was told about, and there is no way to authorize an action against the same
/// <c>security.json</c> right the pipeline checks. Mounting through <c>spark.UseControllers()</c>
/// puts them on the inside.
/// </para>
/// <para>
/// <b>Why a diagnostic and not a runtime check.</b> By the time <c>UseSpark()</c> runs, the app has
/// already called <c>MapControllers()</c> on its own endpoint builder and the resulting endpoints
/// are indistinguishable from any others — nothing at runtime says whether the app opted in. At
/// compile time the call is an ordinary invocation in the app's own source. SPARK004 is the existing
/// precedent for the same asymmetry in the other direction.
/// </para>
/// <para>
/// No cross-file or compilation-end analysis is needed: <c>spark.UseControllers()</c> calls
/// <c>MapControllers()</c> inside <em>Spark's own assembly</em>, which the consuming app's
/// compilation never analyses. So "any <c>MapControllers()</c> here" is already the right predicate.
/// </para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed partial class MapControllersAnalyzer : DiagnosticAnalyzer
{
    private const string MapControllers = "MapControllers";
    private const string ControllerExtensions = "Microsoft.AspNetCore.Builder.ControllerEndpointRouteBuilderExtensions";

    /// <summary>Any Spark type would do; this is the pipeline entry point every Spark app touches.</summary>
    private const string SparkMarker = "MintPlayer.Spark.SparkExtensions";

    /// <summary>Spark's own controllers module, whose <c>UseControllers</c> is the recommended call.</summary>
    private const string ControllersModule = "MintPlayer.Spark.Controllers.SparkControllersExtensions";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [BareMapControllersRule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(static start =>
        {
            if (!AppliesTo(start.Compilation))
                return;

            start.RegisterOperationAction(static operationContext =>
            {
                var invocation = (IInvocationOperation)operationContext.Operation;
                var method = invocation.TargetMethod.ReducedFrom ?? invocation.TargetMethod;

                if (method.Name != MapControllers
                    || method.ContainingType?.ToDisplayString() != ControllerExtensions)
                {
                    return;
                }

                operationContext.ReportDiagnostic(
                    BareMapControllersRule.Create(invocation.Syntax.GetLocation()));
            }, OperationKind.Invocation);
        });
    }

    /// <summary>
    /// A Spark project, but not Spark's controllers module itself. The module is recognised by
    /// <em>declaring</em> the type rather than referencing it, so the exemption cannot be inherited
    /// by an app that merely uses the package.
    /// </summary>
    private static bool AppliesTo(Compilation compilation)
    {
        if (compilation.GetTypeByMetadataName(SparkMarker) is null)
            return false;

        var module = compilation.GetTypeByMetadataName(ControllersModule);
        return module is null
            || !SymbolEqualityComparer.Default.Equals(module.ContainingAssembly, compilation.Assembly);
    }
}
