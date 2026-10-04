using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MintPlayer.Spark.SourceGenerators.Models;
using MintPlayer.SourceGenerators.Tools;

namespace MintPlayer.Spark.SourceGenerators.Generators;

[Generator(LanguageNames.CSharp)]
public class MigrationRegistrationGenerator : IncrementalGenerator
{
    public override void Initialize(
        IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<Settings> settingsProvider)
    {
        // Find all non-abstract classes that implement MintPlayer.Spark.Migrations.ISparkMigration
        var migrationClassesProvider = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, ct) => node is ClassDeclarationSyntax classDecl &&
                    classDecl.BaseList != null &&
                    classDecl.BaseList.Types.Count > 0,
                transform: static (ctx, ct) =>
                {
                    if (ctx.Node is not ClassDeclarationSyntax classDeclaration)
                        return default;

                    var semanticModel = ctx.SemanticModel;
                    var classSymbol = semanticModel.GetDeclaredSymbol(classDeclaration, ct) as INamedTypeSymbol;
                    if (classSymbol == null || classSymbol.IsAbstract)
                        return default;

                    foreach (var iface in classSymbol.AllInterfaces)
                    {
                        if (iface.ToDisplayString() == ReferencedMigrationsReader.MigrationInterfaceMetadataName)
                        {
                            return new MigrationClassInfo
                            {
                                MigrationTypeName = classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                            };
                        }
                    }

                    return default;
                })
            .Where(static x => x != null)
            .Collect();

        // Only emit when the project actually references MintPlayer.Spark.Migrations
        var knowsMigrationsProvider = context.CompilationProvider
            .Select((compilation, ct) =>
                compilation.GetTypeByMetadataName(ReferencedMigrationsReader.MigrationInterfaceMetadataName) != null);

        // Migrations that referenced packages ship (#388), registered after the app's own. Host only.
        var referencedMigrationsProvider = context.CompilationProvider
            .Select(static (compilation, ct) => ReferencedMigrationsReader.Read(compilation, ct));

        var sourceProvider = migrationClassesProvider
            .Combine(knowsMigrationsProvider)
            .Combine(referencedMigrationsProvider)
            .Combine(settingsProvider)
            .Select(static (providers, ct) =>
            {
                var migrationClasses = providers.Left.Left.Left;
                var knowsMigrations = providers.Left.Left.Right;
                var referencedMigrations = providers.Left.Right;
                var settings = providers.Right;

                var own = migrationClasses.Where(x => x != null).Cast<MigrationClassInfo>().ToList();
                var ownNames = new HashSet<string>(own.Select(m => m.MigrationTypeName), StringComparer.Ordinal);
                var all = own.Concat(referencedMigrations.Migrations.Where(m => !ownNames.Contains(m.MigrationTypeName)));

                return (Producer)new MigrationRegistrationProducer(
                    all,
                    knowsMigrations,
                    settings.RootNamespace ?? "GeneratedCode");
            });

        context.ProduceCode(sourceProvider);
    }
}
