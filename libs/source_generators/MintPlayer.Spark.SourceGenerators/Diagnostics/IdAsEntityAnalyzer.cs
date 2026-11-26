using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using System.Collections.Immutable;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

/// <summary>
/// SPARK065: an <c>Id&lt;T&gt;</c> passed where a session expects an entity (#536 D23).
/// </summary>
/// <remarks>
/// RavenDB's <c>Delete&lt;T&gt;(T entity)</c>, <c>StoreAsync(object entity)</c> and <c>Evict&lt;T&gt;(T entity)</c>
/// accept anything, so <c>session.Delete(Setting.IdFor(key))</c> compiles once the id is typed and throws at runtime
/// ("… is not associated with the session, cannot delete unknown entity instance"). Before typed ids the same call
/// bound to <c>Delete(string id)</c>. The fix is <c>.Value</c> on a raw RavenDB session, or the typed
/// <c>Delete&lt;T&gt;(Id&lt;T&gt;)</c> on <c>ISparkSession</c>.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class IdAsEntityAnalyzer : DiagnosticAnalyzer
{
    internal static readonly DiagnosticDescriptor Rule = new(
        id: "SPARK065",
        title: "An Id<T> is passed as an entity",
        messageFormat: "'{0}' is an id, but '{1}' takes an entity here: pass '{0}.Value' to a RavenDB session, or use the typed overload of ISparkSession",
        category: "Correctness",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A session's entity parameter accepts any object, so a typed id compiles there and fails at runtime because the session does not track it.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterOperationAction(Analyze, OperationKind.Invocation);
    }

    private static void Analyze(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        foreach (var argument in invocation.Arguments)
        {
            if (argument.Parameter?.Name != "entity")
                continue;
            var value = argument.Value;
            while (value is IConversionOperation conversion)
                value = conversion.Operand;
            if (!IsId(value.Type))
                continue;
            context.ReportDiagnostic(Diagnostic.Create(Rule, argument.Syntax.GetLocation(), argument.Syntax.ToString(), invocation.TargetMethod.Name));
        }
    }

    private static bool IsId(ITypeSymbol? type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            type = nullable.TypeArguments[0];
        return type is INamedTypeSymbol { Name: "Id", Arity: 1 } named
            && named.ContainingNamespace.ToDisplayString() == "MintPlayer.Spark.Abstractions";
    }
}
