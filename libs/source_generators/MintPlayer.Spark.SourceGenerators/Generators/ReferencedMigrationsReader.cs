using Microsoft.CodeAnalysis;
using MintPlayer.Spark.SourceGenerators.Models;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace MintPlayer.Spark.SourceGenerators.Generators;

/// <summary>
/// Finds the migrations referenced packages ship (#388): a package such as Authorization can carry an
/// <c>ISparkMigration</c> for its own documents, which the app's generated <c>AddMigrations()</c>
/// then registers next to the app's own. Shared (as a linked source file) by <c>MigrationRegistrationGenerator</c> and
/// AllFeatures' <c>SparkFullGenerator</c>.
/// </summary>
/// <remarks>
/// Only assemblies that themselves reference <c>MintPlayer.Spark.Migrations</c> are walked, which keeps
/// the scan off the BCL and third-party packages. A migration must be <c>public</c>: the app's generated
/// code names it. SPARK037 warns about a non-public one in a library, which this would otherwise skip.
/// </remarks>
internal static class ReferencedMigrationsReader
{
    internal const string MigrationInterfaceMetadataName = "MintPlayer.Spark.Migrations.ISparkMigration";
    private const string MigrationsAssemblyName = "MintPlayer.Spark.Migrations";

    /// <summary>Whether <paramref name="compilation"/> builds an application (the only place registrations are emitted).</summary>
    public static bool IsHost(Compilation compilation)
        => compilation.Options.OutputKind is OutputKind.ConsoleApplication or OutputKind.WindowsApplication;

    public static ReferencedMigrationsInfo Read(Compilation compilation, CancellationToken cancellationToken)
    {
        var result = new ReferencedMigrationsInfo();
        if (!IsHost(compilation))
            return result;

        var migrationInterface = compilation.GetTypeByMetadataName(MigrationInterfaceMetadataName);
        if (migrationInterface is null)
            return result;

        var names = new SortedSet<string>(System.StringComparer.Ordinal);
        var seen = new HashSet<IAssemblySymbol>(SymbolEqualityComparer.Default);
        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seen.Add(assembly) || !ReferencesMigrations(assembly))
                continue;

            foreach (var type in AllTypes(assembly.GlobalNamespace))
            {
                if (type.TypeKind != TypeKind.Class || type.IsAbstract || type.IsGenericType)
                    continue;
                if (!IsPubliclyVisible(type))
                    continue;
                if (type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, migrationInterface)))
                    names.Add(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            }
        }

        result.Migrations = names.Select(n => new MigrationClassInfo { MigrationTypeName = n }).ToList();
        return result;
    }

    private static bool ReferencesMigrations(IAssemblySymbol assembly)
        => assembly.Modules.Any(m => m.ReferencedAssemblies.Any(r => r.Name == MigrationsAssemblyName));

    private static bool IsPubliclyVisible(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility != Accessibility.Public)
                return false;
        }
        return true;
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
        {
            foreach (var type in AllTypes(child))
                yield return type;
        }
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
