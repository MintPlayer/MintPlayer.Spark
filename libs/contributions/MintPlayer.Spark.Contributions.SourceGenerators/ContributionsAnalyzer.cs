using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;

namespace MintPlayer.Spark.Contributions.SourceGenerators;

/// <summary>
/// Reports the <c>[Contribution]</c> rules (PRD T2, T4, Q4): SPARK025–SPARK029, SPARK031–SPARK035.
/// </summary>
/// <remarks>
/// <para>
/// A symbol action on properties, so the diagnostics are live and the IDE offers the code fixes
/// (a compilation-end action would not be; see docs/diagnostics.md). Everything is reported on the
/// <c>[Contribution]</c> property, which the analyzed compilation always owns.
/// </para>
/// <para>
/// The reading is <see cref="ContributionInspector"/>'s, the same the generator uses: a declaration
/// with an error here produces no generated code, and one without errors always does.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ContributionsAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
    [
        ContributionDiagnostics.SlotTypeNotAllowed,
        ContributionDiagnostics.CardinalityMismatch,
        ContributionDiagnostics.NoValueProperties,
        ContributionDiagnostics.OwnerNotAnEntity,
        ContributionDiagnostics.MissingJsonIgnore,
        ContributionDiagnostics.ElementNotPartial,
        ContributionDiagnostics.ElementExternal,
        ContributionDiagnostics.NotSoftDeletable,
        ContributionDiagnostics.UnsupportedShape,
        ContributionDiagnostics.ReservedMember,
    ];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

        context.RegisterCompilationStartAction(start =>
        {
            var attributeType = start.Compilation.GetTypeByMetadataName(ContributionInspector.ContributionAttributeMetadataName);
            if (attributeType is null)
                return; // Contributions.Abstractions is not referenced: nothing can be a contribution.

            start.RegisterSymbolAction(symbolContext =>
            {
                var property = (IPropertySymbol)symbolContext.Symbol;
                var attribute = property.GetAttributes()
                    .FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attributeType));
                if (attribute is null)
                    return;

                var (_, issues) = ContributionInspector.Inspect(property, attribute, symbolContext.Compilation, symbolContext.CancellationToken);
                foreach (var issue in issues)
                    symbolContext.ReportDiagnostic(issue.ToDiagnostic());
            }, SymbolKind.Property);
        });
    }
}
