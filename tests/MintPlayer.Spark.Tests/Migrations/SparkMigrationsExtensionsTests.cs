using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Migrations;

namespace MintPlayer.Spark.Tests.Migrations;

/// <summary>
/// Pure DI coverage for <see cref="SparkMigrationsExtensions.AddMigrations(ISparkBuilder, Action{ISparkMigrationsBuilder})"/>:
/// the registry singleton + startup middleware are wired exactly once and re-used across calls, and
/// each migration type is registered as scoped (via <c>TryAddScoped</c> inside the builder).
/// Uses the real <see cref="SparkBuilder"/> so the <c>ISparkBuilder.Registry</c> is a genuine
/// <see cref="SparkModuleRegistry"/> — middleware registrations are counted via reflection over the
/// registry's private action list (running the action would invoke the runner and need a live store).
/// </summary>
public class SparkMigrationsExtensionsTests
{
    private sealed class Mig_A : ISparkMigration
    {
        public static long Version => 202601010001;
        public Task UpAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Mig_B : ISparkMigration
    {
        public static long Version => 202601010002;
        public Task UpAsync(CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public void AddMigrations_registers_registry_singleton_and_migration()
    {
        var builder = new SparkBuilder(new ServiceCollection());

        builder.AddMigrations(m => m.AddMigration<Mig_A>());

        var registryDescriptor = builder.Services
            .Single(d => d.ServiceType == typeof(SparkMigrationRegistry));
        registryDescriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);

        var registry = (SparkMigrationRegistry)registryDescriptor.ImplementationInstance!;
        registry.Migrations.Should().ContainSingle()
            .Which.Version.Should().Be(Mig_A.Version);

        // Exactly one startup middleware was recorded on the builder's registry.
        MiddlewareCount(builder.Registry).Should().Be(1);
    }

    [Fact]
    public void AddMigrations_is_idempotent_across_calls()
    {
        var builder = new SparkBuilder(new ServiceCollection());

        builder.AddMigrations(m => m.AddMigration<Mig_A>());
        builder.AddMigrations(m => m.AddMigration<Mig_B>());

        // Only ONE registry descriptor — the second call re-used the existing singleton.
        var descriptors = builder.Services
            .Where(d => d.ServiceType == typeof(SparkMigrationRegistry))
            .ToArray();
        descriptors.Should().ContainSingle();

        var registry = (SparkMigrationRegistry)descriptors[0].ImplementationInstance!;
        registry.Migrations.Select(x => x.Version)
            .Should().Equal(Mig_A.Version, Mig_B.Version);

        // The startup middleware was wired a single time despite two AddMigrations calls.
        MiddlewareCount(builder.Registry).Should().Be(1);
    }

    [Fact]
    public void AddMigration_registers_the_migration_type_as_scoped()
    {
        var builder = new SparkBuilder(new ServiceCollection());

        builder.AddMigrations(m => m.AddMigration<Mig_A>());

        var migDescriptor = builder.Services
            .Single(d => d.ServiceType == typeof(Mig_A));
        migDescriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    // SparkModuleRegistry keeps its middleware actions in a private List<Action<IApplicationBuilder>>.
    // We count registrations by reflection so we never have to actually run the startup hook (which
    // would call SparkMigrationRunner.RunAtStartup and need a live IDocumentStore).
    private static int MiddlewareCount(SparkModuleRegistry registry)
    {
        var field = typeof(SparkModuleRegistry)
            .GetField("middlewareActions",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var list = (System.Collections.IList)field.GetValue(registry)!;
        return list.Count;
    }
}
