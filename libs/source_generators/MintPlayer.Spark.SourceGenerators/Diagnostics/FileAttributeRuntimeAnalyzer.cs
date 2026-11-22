using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using MintPlayer.Spark.SourceGenerators.Generators;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

/// <summary>
/// SPARK063: an entity has a stored-file property (<c>SparkFile</c>/<c>SparkImage</c>, or a collection of
/// them), but the application does not reference <c>MintPlayer.Spark.Files</c> (#700 D2b).
/// </summary>
/// <remarks>
/// The types live in <c>MintPlayer.Spark.Model</c>, so an entity compiles without the plugin and nothing
/// in it says the bytes need somewhere to go. <c>UseSpark()</c> refuses such a model at startup; this
/// says it at build time. The SPARK039 pattern: checked in the host only, and a referenced runtime
/// assembly is taken as registered (whether <c>AddFiles()</c> is called is not visible here).
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class FileAttributeRuntimeAnalyzer : DiagnosticAnalyzer
{
    internal static readonly DiagnosticDescriptor MissingFilesRule = new(
        id: "SPARK063",
        title: "An entity has a file attribute but the application does not reference MintPlayer.Spark.Files",
        messageFormat: "'{0}.{1}' is a file attribute ({2}), but this application does not reference MintPlayer.Spark.Files, so the app refuses to start. Reference MintPlayer.Spark.Files and call builder.AddFiles() in AddSpark.",
        category: "Spark",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    private const string FilesAssembly = "MintPlayer.Spark.Files";
    private const string ModelAssembly = "MintPlayer.Spark.Model";
    private const string SparkFileName = "MintPlayer.Spark.Abstractions.SparkFile";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [MissingFilesRule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationAction(compilationContext =>
        {
            var compilation = compilationContext.Compilation;
            if (!ReferencedMigrationsReader.IsHost(compilation))
                return;

            if (compilation.SourceModule.ReferencedAssemblySymbols.Any(static a => a.Identity.Name == FilesAssembly))
                return;

            var sparkFile = compilation.GetTypeByMetadataName(SparkFileName);
            if (sparkFile is null)
                return;

            // One report, naming the first file property found: the fix is the same for all.
            var offender = FindFileProperty(compilation, sparkFile);
            if (offender is null)
                return;

            var (property, fileType) = offender.Value;
            var location = property.Locations.FirstOrDefault(static l => l.IsInSource) ?? Location.None;
            compilationContext.ReportDiagnostic(Diagnostic.Create(
                MissingFilesRule, location, property.ContainingType.Name, property.Name, fileType.Name));
        });
    }

    private static (IPropertySymbol Property, INamedTypeSymbol FileType)? FindFileProperty(Compilation compilation, INamedTypeSymbol sparkFile)
    {
        var assemblies = new[] { compilation.Assembly }.Concat(compilation.SourceModule.ReferencedAssemblySymbols);
        foreach (var assembly in assemblies)
        {
            if (!CanHoldEntities(assembly, compilation))
                continue;

            foreach (var type in AllTypes(assembly.GlobalNamespace))
            {
                if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct) || type.IsAbstract)
                    continue;

                foreach (var property in type.GetMembers().OfType<IPropertySymbol>())
                {
                    if (property.DeclaredAccessibility != Accessibility.Public || property.IsStatic)
                        continue;
                    if (FileTypeOf(property.Type, sparkFile) is { } fileType)
                        return (property, fileType);
                }
            }
        }
        return null;
    }

    /// <summary>The SparkFile-derived type of <paramref name="type"/>, looking through nullable, arrays and generic collections.</summary>
    private static INamedTypeSymbol? FileTypeOf(ITypeSymbol type, INamedTypeSymbol sparkFile)
    {
        if (type is IArrayTypeSymbol array)
            return FileTypeOf(array.ElementType, sparkFile);

        if (type is not INamedTypeSymbol named)
            return null;

        if (named.IsGenericType && named.TypeArguments.Length == 1)
            return FileTypeOf(named.TypeArguments[0], sparkFile);

        for (var current = named; current is not null; current = current.BaseType)
            if (SymbolEqualityComparer.Default.Equals(current, sparkFile))
                return named;

        return null;
    }

    private static bool CanHoldEntities(IAssemblySymbol assembly, Compilation compilation)
    {
        if (SymbolEqualityComparer.Default.Equals(assembly, compilation.Assembly))
            return true;
        // The framework's own assemblies hold no application entities.
        if (assembly.Identity.Name.StartsWith("MintPlayer.Spark", System.StringComparison.Ordinal))
            return false;
        // Only an assembly that references the Model package can declare a SparkFile property.
        return assembly.Modules.Any(static m => m.ReferencedAssemblies.Any(static r => r.Name == ModelAssembly));
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
