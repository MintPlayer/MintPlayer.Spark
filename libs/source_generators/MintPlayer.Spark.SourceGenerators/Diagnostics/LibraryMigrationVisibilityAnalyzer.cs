using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using MintPlayer.Spark.SourceGenerators.Generators;
using System.Collections.Immutable;
using System.Linq;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

/// <summary>
/// SPARK037: a library declares an <c>ISparkMigration</c> that is not <c>public</c> (#388).
/// </summary>
/// <remarks>
/// A library's migrations are registered by the application's generated <c>AddMigrations()</c>,
/// which names each class. A non-public one cannot be named there, so it would be skipped without a
/// word and never run. Only libraries are checked: an application registers its own migrations from
/// source and can see internal ones.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LibraryMigrationVisibilityAnalyzer : DiagnosticAnalyzer
{
    internal static readonly DiagnosticDescriptor NonPublicRule = new(
        id: "SPARK037",
        title: "A library's migration must be public",
        messageFormat: "Migration '{0}' is declared in a library but is not public. The application's generated AddMigrations() cannot register it, so it would never run. Make it (and any containing type) public.",
        category: "Spark",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [NonPublicRule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            if (ReferencedMigrationsReader.IsHost(start.Compilation))
                return;
            var migrationInterface = start.Compilation.GetTypeByMetadataName(ReferencedMigrationsReader.MigrationInterfaceMetadataName);
            if (migrationInterface is null)
                return;

            start.RegisterSymbolAction(symbolContext =>
            {
                var type = (INamedTypeSymbol)symbolContext.Symbol;
                if (type.TypeKind != TypeKind.Class || type.IsAbstract)
                    return;
                if (!type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, migrationInterface)))
                    return;
                if (IsPubliclyVisible(type))
                    return;

                symbolContext.ReportDiagnostic(Diagnostic.Create(NonPublicRule, type.Locations.FirstOrDefault(), type.ToDisplayString()));
            }, SymbolKind.NamedType);
        });
    }

    private static bool IsPubliclyVisible(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility != Accessibility.Public)
                return false;
        }
        return true;
    }
}
