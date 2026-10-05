using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using MintPlayer.Spark.SourceGenerators.Generators;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

/// <summary>
/// SPARK039: the application has entities implementing a framework-stamped interface
/// (<c>IAuditCreated</c>/<c>IAuditModified</c>, <c>ISoftDeletable</c>, <c>IModeratable</c>), but does not
/// reference the package that stamps it (#271, D8).
/// </summary>
/// <remarks>
/// The interfaces live in dependency-free <c>.Abstractions</c> packages so an entity library can declare
/// them, and since #271 their members are generated, so nothing in the entity reminds the author that the
/// runtime half is a separate package. Without it the members exist and are never stamped (nor, for
/// soft delete, is a delete ever replaced). Checked in the application only: a library is expected to
/// reference just the abstractions. A referenced runtime assembly is taken as registered — whether its
/// <c>Add…()</c> call is made is not visible here.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StampedContractRuntimeAnalyzer : DiagnosticAnalyzer
{
    internal static readonly DiagnosticDescriptor MissingRuntimeRule = new(
        id: "SPARK039",
        title: "An entity implements a framework-stamped interface whose package the application does not reference",
        messageFormat: "'{0}' implements {1}, but this application does not reference {2}, so it is never stamped. Reference {2} and call builder.{3}() in AddSpark.",
        category: "Spark",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    private static readonly (string[] Interfaces, string RuntimeAssembly, string Registration)[] Contracts =
    [
        (["MintPlayer.Spark.History.IAuditCreated", "MintPlayer.Spark.History.IAuditModified"], "MintPlayer.Spark.History", "AddHistory"),
        (["MintPlayer.Spark.SoftDelete.ISoftDeletable"], "MintPlayer.Spark.SoftDelete", "AddSoftDelete"),
        (["MintPlayer.Spark.Moderation.IModeratable"], "MintPlayer.Spark.Moderation", "AddModeration"),
    ];

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [MissingRuntimeRule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationAction(compilationContext =>
        {
            var compilation = compilationContext.Compilation;
            if (!ReferencedMigrationsReader.IsHost(compilation))
                return;

            var referenced = new HashSet<string>(
                compilation.SourceModule.ReferencedAssemblySymbols.Select(static a => a.Identity.Name),
                System.StringComparer.Ordinal);

            foreach (var (interfaces, runtime, registration) in Contracts)
            {
                if (referenced.Contains(runtime))
                    continue;

                // One report per missing package, naming the first entity found: the fix is the same for all.
                var offender = FindImplementer(compilation, interfaces);
                if (offender is null)
                    continue;

                var location = offender.Locations.FirstOrDefault(static l => l.IsInSource) ?? Location.None;
                var implemented = string.Join(", ", interfaces
                    .Where(name => offender.AllInterfaces.Any(i => i.ToDisplayString() == name))
                    .Select(static name => name.Substring(name.LastIndexOf('.') + 1)));
                compilationContext.ReportDiagnostic(Diagnostic.Create(
                    MissingRuntimeRule, location, offender.ToDisplayString(), implemented, runtime, registration));
            }
        });
    }

    private static INamedTypeSymbol? FindImplementer(Compilation compilation, string[] interfaces)
    {
        var assemblies = new[] { compilation.Assembly }.Concat(compilation.SourceModule.ReferencedAssemblySymbols);
        foreach (var assembly in assemblies)
        {
            // Only assemblies that can see an interface can implement it.
            if (!ReferencesAbstractions(assembly, compilation))
                continue;

            foreach (var type in AllTypes(assembly.GlobalNamespace))
            {
                if (type.TypeKind is TypeKind.Class or TypeKind.Struct && !type.IsAbstract
                    && type.AllInterfaces.Any(i => interfaces.Contains(i.ToDisplayString())))
                    return type;
            }
        }
        return null;
    }

    private static bool ReferencesAbstractions(IAssemblySymbol assembly, Compilation compilation)
    {
        if (SymbolEqualityComparer.Default.Equals(assembly, compilation.Assembly))
            return true;
        // The framework's own assemblies hold no application entities.
        if (assembly.Identity.Name.StartsWith("MintPlayer.Spark", System.StringComparison.Ordinal))
            return false;
        return assembly.Modules.Any(static m => m.ReferencedAssemblies.Any(static r => r.Name.EndsWith(".Abstractions", System.StringComparison.Ordinal)
            && r.Name.StartsWith("MintPlayer.Spark.", System.StringComparison.Ordinal)));
    }

    private static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            yield return type;
            foreach (var nested in Nested(type))
                yield return nested;
        }
        foreach (var child in ns.GetNamespaceMembers())
            foreach (var type in AllTypes(child))
                yield return type;
    }

    private static IEnumerable<INamedTypeSymbol> Nested(INamedTypeSymbol type)
    {
        foreach (var nested in type.GetTypeMembers())
        {
            yield return nested;
            foreach (var deeper in Nested(nested))
                yield return deeper;
        }
    }
}
