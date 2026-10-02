using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Immutable;
using System.Linq;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

/// <summary>
/// SPARK023: a custom action named like a reserved <c>security.json</c> verb (#460 contributions, Q2).
/// </summary>
/// <remarks>
/// <para>
/// A custom action's right is <c>{ActionName}/{Type}</c>, and its name is its class name without the
/// <c>Action</c> suffix (the registration generator and <c>CustomActionResolver</c> derive it the same
/// way). A class <c>RestoreAction</c> would therefore be authorized by <c>Restore/Car</c> — the right
/// that also authorizes SoftDelete's restore — and one of the two would shadow the other. The verbs
/// come from every <c>[assembly: SparkReservedActions(typeof(X))]</c> in the compilation and its
/// references, so a package's verbs are reserved exactly where the package is referenced.
/// </para>
/// <para>
/// An error because <c>UseSpark()</c> refuses the same application at startup.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ReservedActionNameAnalyzer : DiagnosticAnalyzer
{
    private const string CustomActionMetadataName = "MintPlayer.Spark.Abstractions.Actions.ICustomAction";

    internal static readonly DiagnosticDescriptor ReservedNameRule = new(
        id: "SPARK023",
        title: "Custom action is named like a reserved action verb",
        messageFormat: "Custom action '{0}' is named '{1}', which is the reserved action verb '{2}' declared by {3} in '{4}'. It would share that verb's right; rename the class.",
        category: "Security",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A custom action is authorized by '{ActionName}/{Type}'. When the name equals a verb Spark or a package asks for (compared case-insensitively, like rights), one right authorizes both, and the application refuses to start.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [ReservedNameRule];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

        context.RegisterCompilationStartAction(start =>
        {
            var customAction = start.Compilation.GetTypeByMetadataName(CustomActionMetadataName);
            if (customAction is null)
                return; // Spark.Abstractions is not referenced: nothing can be a custom action.

            var reserved = ReservedActionsReader.Read(start.Compilation);
            if (reserved.IsEmpty)
                return;

            start.RegisterSymbolAction(symbolContext =>
            {
                if (symbolContext.Symbol is not INamedTypeSymbol { TypeKind: TypeKind.Class, IsAbstract: false } type)
                    return;
                if (!type.AllInterfaces.Contains(customAction, SymbolEqualityComparer.Default))
                    return;

                var actionName = ActionName(type.Name);
                var collision = reserved.FirstOrDefault(r => string.Equals(r.Verb, actionName, StringComparison.OrdinalIgnoreCase));
                if (collision is null)
                    return;

                symbolContext.ReportDiagnostic(Diagnostic.Create(
                    ReservedNameRule, type.Locations.FirstOrDefault(), type.Name, actionName, collision.Verb, collision.DeclaringType, collision.Assembly));
            }, SymbolKind.NamedType);
        });
    }

    /// <summary>The name a custom action class answers to: its name without an <c>Action</c> suffix (ordinal).</summary>
    internal static string ActionName(string className)
        => className.EndsWith("Action", StringComparison.Ordinal)
            ? className.Substring(0, className.Length - "Action".Length)
            : className;
}
