using MintPlayer.Spark.Migrations;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

public class MigrationRegistrationGeneratorTests
{
    private const string GeneratorName = "MigrationRegistrationGenerator";

    [Fact]
    public void ISparkMigration_implementer_is_registered()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using MintPlayer.Spark.Migrations;

            namespace TestApp.Migrations;

            public class AddInitialData : ISparkMigration
            {
                public static long Version => 202606081200;
                public Task UpAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """;

        var result = GeneratorHarness.Run(
            GeneratorName,
            [source],
            referenceTypes: [typeof(ISparkMigration)],
            rootNamespace: "TestApp");

        result.GeneratedSources.Should().ContainSingle();
        var generated = result.GeneratedSources[0].Source;

        generated.Should().Contain("AddMigrations");
        generated.Should().Contain("migrations.AddMigration<global::TestApp.Migrations.AddInitialData>()");
    }

    [Fact]
    public void No_source_without_Migrations_reference()
    {
        var source = """
            namespace TestApp;
            public class Foo { }
            """;

        var result = GeneratorHarness.Run(
            GeneratorName,
            [source],
            referenceTypes: Array.Empty<Type>(),
            rootNamespace: "TestApp");

        result.GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public void Multiple_migrations_are_all_registered()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using MintPlayer.Spark.Migrations;

            namespace TestApp.Migrations;

            public class AddInitialData : ISparkMigration
            {
                public static long Version => 202606081200;
                public Task UpAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            }

            public class SeedLookups : ISparkMigration
            {
                public static long Version => 202606081300;
                public Task UpAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """;

        var result = GeneratorHarness.Run(
            GeneratorName,
            [source],
            referenceTypes: [typeof(ISparkMigration)],
            rootNamespace: "TestApp");

        result.GeneratedSources.Should().ContainSingle();
        var generated = result.GeneratedSources[0].Source;

        generated.Should().Contain("migrations.AddMigration<global::TestApp.Migrations.AddInitialData>()");
        generated.Should().Contain("migrations.AddMigration<global::TestApp.Migrations.SeedLookups>()");
    }

    [Fact]
    public void Abstract_implementer_is_skipped()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using MintPlayer.Spark.Migrations;

            namespace TestApp.Migrations;

            public abstract class MigrationBase : ISparkMigration
            {
                public static long Version => 0;
                public Task UpAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """;

        var result = GeneratorHarness.Run(
            GeneratorName,
            [source],
            referenceTypes: [typeof(ISparkMigration)],
            rootNamespace: "TestApp");

        // The only implementer is abstract — the generator skips it, leaving zero
        // migration classes, so nothing is emitted.
        result.GeneratedSources.Should().BeEmpty();
    }

    // ── Migrations a referenced package ships (#388) ──────────────────────────────

    private const string AppWithoutMigrations = """
        namespace TestApp;
        public static class Program { public static void Main() { } }
        """;

    private static Microsoft.CodeAnalysis.MetadataReference Package(string accessibility) =>
        GeneratorHarness.CompileToMetadataReference(
            "Acme.Package",
            [$$"""
                using System.Threading;
                using System.Threading.Tasks;
                using MintPlayer.Spark.Migrations;

                namespace Acme.Package.Migrations;

                {{accessibility}} class M_202610041800_Fix : ISparkMigration
                {
                    public static long Version => 202610041800;
                    public Task UpAsync(CancellationToken cancellationToken) => Task.CompletedTask;
                }
                """],
            [typeof(ISparkMigration)]);

    [Fact]
    public void A_host_registers_the_public_migrations_of_a_referenced_package()
    {
        var result = GeneratorHarness.Run(
            GeneratorName,
            [AppWithoutMigrations],
            referenceTypes: [typeof(ISparkMigration)],
            rootNamespace: "TestApp",
            outputKind: Microsoft.CodeAnalysis.OutputKind.ConsoleApplication,
            additionalReferences: [Package("public")]);

        // The app has no migration of its own, yet still gets AddMigrations() (QnA's shape).
        result.GeneratedSources.Should().ContainSingle();
        result.GeneratedSources[0].Source.Should()
            .Contain("migrations.AddMigration<global::Acme.Package.Migrations.M_202610041800_Fix>()");
    }

    [Fact]
    public void A_host_registers_its_own_migrations_before_the_packages()
    {
        var own = """
            using System.Threading;
            using System.Threading.Tasks;
            using MintPlayer.Spark.Migrations;

            namespace TestApp.Migrations;

            public class M_202601010000_Own : ISparkMigration
            {
                public static long Version => 202601010000;
                public Task UpAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """;

        var result = GeneratorHarness.Run(
            GeneratorName,
            [AppWithoutMigrations, own],
            referenceTypes: [typeof(ISparkMigration)],
            rootNamespace: "TestApp",
            outputKind: Microsoft.CodeAnalysis.OutputKind.ConsoleApplication,
            additionalReferences: [Package("public")]);

        var generated = result.GeneratedSources.Should().ContainSingle().Which.Source;
        var ownAt = generated.IndexOf("M_202601010000_Own", StringComparison.Ordinal);
        var packageAt = generated.IndexOf("M_202610041800_Fix", StringComparison.Ordinal);
        ownAt.Should().BeGreaterThan(-1);
        packageAt.Should().BeGreaterThan(ownAt);
    }

    [Fact]
    public void A_non_public_package_migration_cannot_be_registered()
    {
        var result = GeneratorHarness.Run(
            GeneratorName,
            [AppWithoutMigrations],
            referenceTypes: [typeof(ISparkMigration)],
            rootNamespace: "TestApp",
            outputKind: Microsoft.CodeAnalysis.OutputKind.ConsoleApplication,
            additionalReferences: [Package("internal")]);

        // The generated code could not name it; SPARK037 warns in the package instead.
        result.GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public void A_library_does_not_register_a_referenced_package_migration()
    {
        // Only the application registers: a library between the package and the app must not
        // emit a second AddMigrations() for migrations that are not its own.
        var result = GeneratorHarness.Run(
            GeneratorName,
            ["namespace TestLib; public class Foo { }"],
            referenceTypes: [typeof(ISparkMigration)],
            rootNamespace: "TestLib",
            additionalReferences: [Package("public")]);

        result.GeneratedSources.Should().BeEmpty();
    }
}
