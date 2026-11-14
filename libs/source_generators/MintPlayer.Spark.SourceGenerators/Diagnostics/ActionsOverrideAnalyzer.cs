using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using MintPlayer.Spark.SourceGenerators.Generators;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

/// <summary>
/// #660 G17: actions classes that take over, or collide with, a referenced library's actions class of the same name.
/// </summary>
/// <remarks>
/// The run-time resolver finds an entity's actions class by its simple name (<c>{Entity}Actions</c>) and refuses two
/// concrete matches, unless the generator recorded one as specialising the others
/// (<c>[assembly: SparkActionsOverride]</c>). These rules report at build time what would otherwise refuse at run time.
/// <list type="bullet">
///   <item><b>SPARK061</b> (error): this project specialises a same-named actions class, and a referenced assembly
///   already records a <i>different</i> override for the same entity that is not one of this class's base classes.
///   Two siblings each claim the entity, and the resolver refuses both.</item>
///   <item><b>SPARK062</b> (warning): this project declares an actions class whose name a referenced assembly's
///   concrete actions class already has, without deriving from it. The resolver refuses the ambiguity at run time; and
///   deriving would have kept the library's own rules (its row filter, its protected attributes).</item>
/// </list>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ActionsOverrideAnalyzer : DiagnosticAnalyzer
{
    private const string DefaultActionsMetadataName = "MintPlayer.Spark.Actions.DefaultPersistentObjectActions`1";
    private const string OverrideAttributeMetadataName = "MintPlayer.Spark.Abstractions.SparkActionsOverrideAttribute";

    internal static readonly DiagnosticDescriptor SiblingOverrideRule = new(
        id: "SPARK061",
        title: "Two projects take over the same entity's actions",
        messageFormat: "'{0}' takes over the actions of '{1}', but '{2}' (assembly '{3}') already does and is not a base class of '{0}'. The resolver refuses both at run time. Derive '{0}' from '{2}' instead.",
        category: "Spark",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor SameNameRule = new(
        id: "SPARK062",
        title: "An actions class shares a referenced library's actions class name without deriving from it",
        messageFormat: "'{0}' has the same name as '{1}' (assembly '{2}'), the actions class that library ships for '{3}', but does not derive from it. The resolver refuses the two as ambiguous at run time. Derive from '{1}' to take it over and keep its rules, or rename '{0}'.",
        category: "Spark",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [SiblingOverrideRule, SameNameRule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var defaultActions = start.Compilation.GetTypeByMetadataName(DefaultActionsMetadataName);
            if (defaultActions is null) return;
            var overrideAttribute = start.Compilation.GetTypeByMetadataName(OverrideAttributeMetadataName);
            var referenced = start.Compilation.SourceModule.ReferencedAssemblySymbols;

            start.RegisterSymbolAction(symbolContext =>
            {
                if (symbolContext.Symbol is not INamedTypeSymbol type || type.IsAbstract || type.TypeKind != TypeKind.Class)
                    return;
                if (EntityOf(type, defaultActions) is not { } entity)
                    return;

                if (ActionsRegistrationGenerator.HasSameNamedActionsAncestor(type))
                    ReportSiblings(symbolContext, type, entity, referenced, overrideAttribute);
                else
                    ReportSameName(symbolContext, type, entity, referenced, defaultActions);
            }, SymbolKind.NamedType);
        });
    }

    private static void ReportSiblings(
        SymbolAnalysisContext context, INamedTypeSymbol type, ITypeSymbol entity,
        ImmutableArray<IAssemblySymbol> referenced, INamedTypeSymbol? overrideAttribute)
    {
        if (overrideAttribute is null) return;
        var ancestors = new HashSet<ISymbol>(Ancestors(type), SymbolEqualityComparer.Default);

        foreach (var assembly in referenced)
        {
            foreach (var attribute in assembly.GetAttributes())
            {
                if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, overrideAttribute)) continue;
                if (attribute.ConstructorArguments.Length != 2) continue;
                if (attribute.ConstructorArguments[0].Value is not ITypeSymbol claimedEntity
                    || attribute.ConstructorArguments[1].Value is not INamedTypeSymbol claimant) continue;
                if (!SymbolEqualityComparer.Default.Equals(claimedEntity, entity)) continue;
                if (ancestors.Contains(claimant)) continue;

                context.ReportDiagnostic(Diagnostic.Create(
                    SiblingOverrideRule, type.Locations.FirstOrDefault(),
                    type.ToDisplayString(), entity.ToDisplayString(), claimant.ToDisplayString(), assembly.Name));
            }
        }
    }

    private static void ReportSameName(
        SymbolAnalysisContext context, INamedTypeSymbol type, ITypeSymbol entity,
        ImmutableArray<IAssemblySymbol> referenced, INamedTypeSymbol defaultActions)
    {
        foreach (var assembly in referenced)
        {
            // TypeNames is a cheap name set: only walk an assembly that has a type with this name at all.
            if (!assembly.TypeNames.Contains(type.MetadataName)) continue;

            foreach (var other in TypesNamed(assembly.GlobalNamespace, type.MetadataName))
            {
                if (other.IsAbstract || other.DeclaredAccessibility != Accessibility.Public) continue;
                if (EntityOf(other, defaultActions) is not { } otherEntity) continue;

                context.ReportDiagnostic(Diagnostic.Create(
                    SameNameRule, type.Locations.FirstOrDefault(),
                    type.ToDisplayString(), other.ToDisplayString(), assembly.Name, otherEntity.ToDisplayString()));
            }
        }
    }

    /// <summary>The <c>T</c> of the <c>DefaultPersistentObjectActions&lt;T&gt;</c> the type derives from, or null.</summary>
    private static ITypeSymbol? EntityOf(INamedTypeSymbol type, INamedTypeSymbol defaultActions)
    {
        foreach (var ancestor in Ancestors(type))
        {
            if (ancestor.IsGenericType && SymbolEqualityComparer.Default.Equals(ancestor.ConstructedFrom, defaultActions))
                return ancestor.TypeArguments[0];
        }
        return null;
    }

    private static IEnumerable<INamedTypeSymbol> Ancestors(INamedTypeSymbol type)
    {
        for (var ancestor = type.BaseType; ancestor is not null; ancestor = ancestor.BaseType)
            yield return ancestor;
    }

    private static IEnumerable<INamedTypeSymbol> TypesNamed(INamespaceSymbol ns, string metadataName)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            if (type.MetadataName == metadataName)
                yield return type;
        }
        foreach (var child in ns.GetNamespaceMembers())
        {
            foreach (var type in TypesNamed(child, metadataName))
                yield return type;
        }
    }
}
