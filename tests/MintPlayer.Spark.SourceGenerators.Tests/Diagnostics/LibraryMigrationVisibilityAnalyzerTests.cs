using MintPlayer.Spark.Migrations;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Diagnostics;

/// <summary>
/// SPARK037 (#388): a library's migration is registered by the application's generated
/// <c>AddMigrations()</c>, which names it, so a non-public one would never run.
/// </summary>
public class LibraryMigrationVisibilityAnalyzerTests
{
    private const string AnalyzerName = "LibraryMigrationVisibilityAnalyzer";

    private static Task<IReadOnlyList<Microsoft.CodeAnalysis.Diagnostic>> Analyze(string source)
        => GeneratorHarness.RunAnalyzerAsync(AnalyzerName, [source], [typeof(ISparkMigration)]);

    private static string Migration(string accessibility, string? container = null)
    {
        var migration = $$"""
            {{accessibility}} class M_202610041800_Fix : ISparkMigration
            {
                public static long Version => 202610041800;
                public Task UpAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """;
        if (container is not null)
            migration = $"{container} class Holder {{ {migration} }}";
        return $$"""
            using System.Threading;
            using System.Threading.Tasks;
            using MintPlayer.Spark.Migrations;

            namespace Acme.Package.Migrations;

            {{migration}}
            """;
    }

    [Fact]
    public async Task An_internal_migration_in_a_library_is_reported()
    {
        var diagnostics = await Analyze(Migration("internal"));

        diagnostics.Should().ContainSingle(d => d.Id == "SPARK037")
            .Which.GetMessage().Should().Contain("M_202610041800_Fix");
    }

    [Fact]
    public async Task A_public_migration_in_a_non_public_type_is_reported()
    {
        var diagnostics = await Analyze(Migration("public", container: "internal static"));

        diagnostics.Should().ContainSingle(d => d.Id == "SPARK037");
    }

    [Fact]
    public async Task A_public_migration_is_not_reported()
    {
        var diagnostics = await Analyze(Migration("public"));

        diagnostics.Where(d => d.Id == "SPARK037").Should().BeEmpty();
    }
}
