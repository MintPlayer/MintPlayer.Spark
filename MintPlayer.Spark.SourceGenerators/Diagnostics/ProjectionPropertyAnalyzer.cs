using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed partial class ProjectionPropertyAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray.Create(PropertyTypeMismatchRule, MissingReferenceAttributeRule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeNamedType, SymbolKind.NamedType);
    }

    private static void AnalyzeNamedType(SymbolAnalysisContext context)
    {
        if (context.Symbol is not INamedTypeSymbol projectionType)
            return;

        if (projectionType.TypeKind != TypeKind.Class)
            return;

        // 1. Find [FromIndex] attribute on this type
        var fromIndexAttrType = context.Compilation.GetTypeByMetadataName(
            "MintPlayer.Spark.Abstractions.FromIndexAttribute");
        if (fromIndexAttrType is null)
            return;

        var fromIndexAttr = projectionType.GetAttributes().FirstOrDefault(a =>
            SymbolEqualityComparer.Default.Equals(a.AttributeClass, fromIndexAttrType));
        if (fromIndexAttr is null)
            return;

        // 2. Get the index type from the attribute constructor argument
        if (fromIndexAttr.ConstructorArguments.Length == 0)
            return;

        var indexType = fromIndexAttr.ConstructorArguments[0].Value as INamedTypeSymbol;
        if (indexType is null)
            return;

        // 3. Walk the index type's base class chain to find AbstractIndexCreationTask<T>
        var entityType = GetEntityTypeFromIndex(indexType);
        if (entityType is null)
            return;

        // 4. Get reference attribute type for later comparison
        var referenceAttrType = context.Compilation.GetTypeByMetadataName(
            "MintPlayer.Spark.Abstractions.ReferenceAttribute");

        // 5. Compare properties by name
        var entityProperties = entityType.GetMembers().OfType<IPropertySymbol>()
            .Where(p => p.DeclaredAccessibility == Accessibility.Public && !p.IsStatic)
            .ToDictionary(p => p.Name);

        var projectionProperties = projectionType.GetMembers().OfType<IPropertySymbol>()
            .Where(p => p.DeclaredAccessibility == Accessibility.Public && !p.IsStatic);

        foreach (var projProp in projectionProperties)
        {
            if (!entityProperties.TryGetValue(projProp.Name, out var entityProp))
                continue; // Property only on projection (e.g., computed FullName) — skip

            var projPropLocation = projProp.Locations.FirstOrDefault(l => l.IsInSource)
                ?? Location.None;

            // 5a. Check type mismatch
            if (!SymbolEqualityComparer.Default.Equals(projProp.Type, entityProp.Type))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    PropertyTypeMismatchRule,
                    projPropLocation,
                    projProp.Name,
                    projectionType.Name,
                    projProp.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    entityType.Name,
                    entityProp.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
            }

            // 5b. Check [Reference] attribute consistency
            if (referenceAttrType is null)
                continue;

            var entityRefAttr = entityProp.GetAttributes().FirstOrDefault(a =>
                SymbolEqualityComparer.Default.Equals(a.AttributeClass, referenceAttrType));
            if (entityRefAttr is null)
                continue; // Entity property has no [Reference] — nothing to check

            var entityRefTarget = entityRefAttr.ConstructorArguments.Length > 0
                ? entityRefAttr.ConstructorArguments[0].Value as INamedTypeSymbol
                : null;
            if (entityRefTarget is null)
                continue;

            var projRefAttr = projProp.GetAttributes().FirstOrDefault(a =>
                SymbolEqualityComparer.Default.Equals(a.AttributeClass, referenceAttrType));

            if (projRefAttr is null)
            {
                // Missing [Reference] entirely
                context.ReportDiagnostic(Diagnostic.Create(
                    MissingReferenceAttributeRule,
                    projPropLocation,
                    projProp.Name,
                    projectionType.Name,
                    entityRefTarget.Name,
                    entityType.Name));
                continue;
            }

            // Check if target type matches
            var projRefTarget = projRefAttr.ConstructorArguments.Length > 0
                ? projRefAttr.ConstructorArguments[0].Value as INamedTypeSymbol
                : null;

            if (projRefTarget is not null &&
                !SymbolEqualityComparer.Default.Equals(projRefTarget, entityRefTarget))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    MissingReferenceAttributeRule,
                    projPropLocation,
                    projProp.Name,
                    projectionType.Name,
                    entityRefTarget.Name,
                    entityType.Name));
            }
        }
    }

    private static INamedTypeSymbol? GetEntityTypeFromIndex(INamedTypeSymbol indexType)
    {
        var current = indexType.BaseType;
        while (current is not null)
        {
            if (current.IsGenericType &&
                current.OriginalDefinition.Name == "AbstractIndexCreationTask" &&
                current.TypeArguments.Length >= 1 &&
                current.TypeArguments[0] is INamedTypeSymbol entityType)
            {
                return entityType;
            }
            current = current.BaseType;
        }
        return null;
    }
}
