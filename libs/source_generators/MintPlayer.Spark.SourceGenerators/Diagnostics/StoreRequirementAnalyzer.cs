using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

/// <summary>
/// SPARK060 (#551 D13): a feature configured next to a store provider that cannot serve it, such as
/// <c>spark.UseInMemory(); spark.AddMessaging();</c>. <c>UseSpark()</c> refuses the same configuration at startup.
/// </summary>
/// <remarks>
/// Both sides are declared by attribute, so this works across assemblies. The provider method carries
/// <c>[SparkStoreProvider]</c> and the feature method carries <c>[SparkRequiresStore]</c>. Only calls in one body are
/// compared, which is typically the <c>AddSpark</c> lambda. A body that configures no provider reports nothing, because
/// the store may be registered elsewhere. That case is left to the startup check.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StoreRequirementAnalyzer : DiagnosticAnalyzer
{
    private const string ProviderAttributeName = "MintPlayer.Spark.Abstractions.Storage.SparkStoreProviderAttribute";
    private const string RequiresAttributeName = "MintPlayer.Spark.Abstractions.Storage.SparkRequiresStoreAttribute";
    private const string CapabilitiesTypeName = "MintPlayer.Spark.Abstractions.Storage.SparkStoreCapabilities";

    internal static readonly DiagnosticDescriptor Rule = new(
        id: "SPARK060",
        title: "Feature configured on a store that cannot serve it",
        messageFormat: "{0}. The host refuses this configuration at startup.",
        category: "Storage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var providerAttribute = start.Compilation.GetTypeByMetadataName(ProviderAttributeName);
            var requiresAttribute = start.Compilation.GetTypeByMetadataName(RequiresAttributeName);
            var capabilitiesType = start.Compilation.GetTypeByMetadataName(CapabilitiesTypeName);
            if (providerAttribute is null || requiresAttribute is null || capabilitiesType is null) return;

            var names = capabilitiesType.GetMembers().OfType<IFieldSymbol>()
                .Where(f => f.HasConstantValue && f.ConstantValue is int value && value != 0)
                .Select(f => (Name: f.Name, Value: (int)f.ConstantValue!))
                .ToArray();

            start.RegisterOperationBlockAction(block =>
            {
                foreach (var root in block.OperationBlocks)
                    AnalyzeBodies(root, block.ReportDiagnostic, providerAttribute, requiresAttribute, names);
            });
        });
    }

    private static void AnalyzeBodies(
        IOperation root, Action<Diagnostic> report, INamedTypeSymbol providerAttribute, INamedTypeSymbol requiresAttribute,
        (string Name, int Value)[] names)
    {
        // Each lambda is its own body, and so is the block outside every lambda.
        var bodies = new List<IOperation> { root };
        bodies.AddRange(root.Descendants().OfType<IAnonymousFunctionOperation>().Select(f => (IOperation)f.Body));

        foreach (var body in bodies)
        {
            var invocations = OwnInvocations(body).ToList();

            var provider = invocations
                .Select(i => i.TargetMethod.GetAttributes().FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, providerAttribute)))
                .FirstOrDefault(a => a is not null);
            if (provider is null
                || provider.ConstructorArguments.Length != 2
                || provider.ConstructorArguments[0].Value is not string providerName
                || provider.ConstructorArguments[1].Value is not int offered)
                continue;

            foreach (var invocation in invocations)
            {
                var requires = invocation.TargetMethod.GetAttributes()
                    .FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, requiresAttribute));
                if (requires is null || requires.ConstructorArguments.Length == 0 || requires.ConstructorArguments[0].Value is not string feature)
                    continue;

                var needed = requires.ConstructorArguments.Length > 1 && requires.ConstructorArguments[1].Value is int n ? n : 0;
                var onlyOn = requires.NamedArguments.FirstOrDefault(a => a.Key == "Provider").Value.Value as string;

                string? message = null;
                if (onlyOn is not null && !string.Equals(onlyOn, providerName, StringComparison.OrdinalIgnoreCase))
                    message = $"{feature} runs on {onlyOn} only; this configuration uses the {providerName} provider";
                else if ((needed & ~offered) is var missing and not 0)
                    message = $"{feature} needs {Describe(missing, names)}; the {providerName} provider doesn't offer it";

                if (message is not null)
                    report(Diagnostic.Create(Rule, invocation.Syntax.GetLocation(), message));
            }
        }
    }

    /// <summary>The invocations of <paramref name="body"/> itself, not those inside a nested lambda.</summary>
    private static IEnumerable<IInvocationOperation> OwnInvocations(IOperation body)
    {
        var stack = new Stack<IOperation>([body]);
        while (stack.Count > 0)
        {
            var operation = stack.Pop();
            if (operation is IInvocationOperation invocation)
                yield return invocation;

            foreach (var child in operation.ChildOperations)
                if (child is not IAnonymousFunctionOperation)
                    stack.Push(child);
        }
    }

    private static string Describe(int flags, (string Name, int Value)[] names)
        => string.Join(", ", names.Where(n => (flags & n.Value) == n.Value).Select(n => n.Name));
}
