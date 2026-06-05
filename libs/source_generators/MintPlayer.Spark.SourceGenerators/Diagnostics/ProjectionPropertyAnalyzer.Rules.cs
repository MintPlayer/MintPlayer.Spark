using Microsoft.CodeAnalysis;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

public sealed partial class ProjectionPropertyAnalyzer
{
    private static readonly DiagnosticDescriptor PropertyTypeMismatchRule = new(
        id: "SPARK001",
        title: "Projection property type mismatch",
        messageFormat: "Property '{0}' on projection type '{1}' has type '{2}' but the corresponding property on entity type '{3}' has type '{4}'",
        category: "Correctness",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Properties on [FromIndex] projection types must have the same type as the corresponding property on the base entity type.");

    private static readonly DiagnosticDescriptor MissingReferenceAttributeRule = new(
        id: "SPARK002",
        title: "Projection property missing [Reference] attribute",
        messageFormat: "Property '{0}' on projection type '{1}' is missing [Reference(typeof({2}))] attribute that exists on entity type '{3}'",
        category: "Correctness",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "When a base entity property has a [Reference] attribute, the corresponding projection property must also have a [Reference] attribute with the same target type.");
}
