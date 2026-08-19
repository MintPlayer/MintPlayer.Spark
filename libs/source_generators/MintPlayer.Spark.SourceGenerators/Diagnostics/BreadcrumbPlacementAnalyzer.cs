using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using MintPlayer.Spark.SourceGenerators.Models;
using System.Collections.Immutable;
using System.Linq;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

/// <summary>
/// Local placement rules for the property-level <c>[Breadcrumb]</c> marker, so entity-library
/// authors get squiggles in the library compilation itself — the generator's SPARK_INDEX_012 fires
/// only in the app compilation that walks the type, and anchors at <c>Location.None</c> for
/// metadata symbols.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BreadcrumbPlacementAnalyzer : DiagnosticAnalyzer
{
    private const string FromIndexAttributeFullName = "MintPlayer.Spark.Abstractions.FromIndexAttribute";

    /// <summary>
    /// Projections are derived artifacts: the generator emits companions from the <em>source</em>
    /// type's marker, and the resolver reads entity values — a marker on a projection member is
    /// never consulted by anything.
    /// </summary>
    internal static readonly DiagnosticDescriptor MarkerInsideProjectionRule = new(
        id: "SPARK007",
        title: "[Breadcrumb] inside a [FromIndex] projection has no effect",
        messageFormat: "'{0}' is a [FromIndex] projection; the [Breadcrumb] marker on '{1}' is never consulted. Mark the property on the source type instead",
        category: "Correctness",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <summary>
    /// Local mirror of the generator-side SPARK_INDEX_012 rejections that are knowable from the
    /// declaration alone: a marker on a collection (no single value to sort by), a [Reference] id
    /// (a map cannot follow a document reference), or a TranslatedString (fans out per language).
    /// </summary>
    internal static readonly DiagnosticDescriptor MarkerOnUnusableKindRule = new(
        id: "SPARK008",
        title: "[Breadcrumb] on a property kind that cannot carry it",
        messageFormat: "[Breadcrumb] on '{0}.{1}' has no effect: {2}",
        category: "Correctness",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray.Create(MarkerInsideProjectionRule, MarkerOnUnusableKindRule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(Analyze, SymbolKind.Property);
    }

    private static void Analyze(SymbolAnalysisContext context)
    {
        var property = (IPropertySymbol)context.Symbol;
        if (!property.HasBreadcrumbMarker())
            return;

        var location = property.Locations.FirstOrDefault() ?? Location.None;
        var containing = property.ContainingType;

        if (containing is not null && containing.GetAttributes().Any(a =>
                a.AttributeClass?.ToDisplayString() == FromIndexAttributeFullName))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                MarkerInsideProjectionRule, location, containing.Name, property.Name));
            return;
        }

        var reason = UnusableReason(property);
        if (reason is not null)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                MarkerOnUnusableKindRule, location, containing?.Name, property.Name, reason));
        }
    }

    private static string? UnusableReason(IPropertySymbol property)
    {
        if (property.IsReferenceProperty())
            return "it is a [Reference] id — an index map cannot follow a document reference, and the id is opaque";

        var type = property.Type.UnwrapNullable();
        if (type.IsTranslatedString())
            return "TranslatedString fans out per language and cannot be a single breadcrumb value";

        if (type.GetCollectionElementType() is not null)
            return "a collection has no single value to sort by";

        return null;
    }
}
