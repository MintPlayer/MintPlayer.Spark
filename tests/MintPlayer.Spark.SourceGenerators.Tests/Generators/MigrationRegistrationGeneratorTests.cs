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
}
