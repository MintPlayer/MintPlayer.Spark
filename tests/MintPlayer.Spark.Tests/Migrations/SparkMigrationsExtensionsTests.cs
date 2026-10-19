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
/// registry's private per-stage action lists (running the action would invoke the runner and need a
/// live store).
/// </summary>
public class SparkMigrationsExtensionsTests
{
    private sealed class M_202601010001_A : ISparkMigration
    {
        public Task UpAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class M_202601010002_B : ISparkMigration
    {
        public Task UpAsync(CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public void AddMigrations_registers_registry_singleton_and_migration()
    {
        var builder = new SparkBuilder(new ServiceCollection());

        builder.AddMigrations(m => m.AddMigration<M_202601010001_A>());

        var registryDescriptor = builder.Services
            .Single(d => d.ServiceType == typeof(SparkMigrationRegistry));
        registryDescriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);

        var registry = (SparkMigrationRegistry)registryDescriptor.ImplementationInstance!;
        registry.Migrations.Should().ContainSingle()
            .Which.Version.Should().Be(202601010001L);

        // Exactly one startup middleware was recorded on the builder's registry.
        MiddlewareCount(builder.Registry).Should().Be(1);
    }

    [Fact]
    public void AddMigrations_is_idempotent_across_calls()
    {
        var builder = new SparkBuilder(new ServiceCollection());

        builder.AddMigrations(m => m.AddMigration<M_202601010001_A>());
        builder.AddMigrations(m => m.AddMigration<M_202601010002_B>());

        // Only ONE registry descriptor — the second call re-used the existing singleton.
        var descriptors = builder.Services
            .Where(d => d.ServiceType == typeof(SparkMigrationRegistry))
            .ToArray();
        descriptors.Should().ContainSingle();

        var registry = (SparkMigrationRegistry)descriptors[0].ImplementationInstance!;
        registry.Migrations.Select(x => x.Version)
            .Should().Equal(202601010001L, 202601010002L);

        // The startup middleware was wired a single time despite two AddMigrations calls.
        MiddlewareCount(builder.Registry).Should().Be(1);
    }

    [Fact]
    public void AddMigration_registers_the_migration_type_as_scoped()
    {
        var builder = new SparkBuilder(new ServiceCollection());

        builder.AddMigrations(m => m.AddMigration<M_202601010001_A>());

        var migDescriptor = builder.Services
            .Single(d => d.ServiceType == typeof(M_202601010001_A));
        migDescriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    // SparkModuleRegistry keeps its middleware actions in a private
    // Dictionary<SparkMiddlewareStage, List<Action<IApplicationBuilder>>>. We count registrations by
    // reflection so we never have to actually run the startup hook (which would call
    // SparkMigrationRunner.RunAtStartup and need a live IDocumentStore).
    //
    // Summed across every stage on purpose. Counting only the default stage would let a future
    // change move this registration to another stage without the idempotency assertions noticing,
    // and "wired exactly once" is a claim about the whole registry, not about one bucket of it.
    private static int MiddlewareCount(SparkModuleRegistry registry)
    {
        var field = typeof(SparkModuleRegistry)
            .GetField("middlewareActions",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var byStage = (System.Collections.IDictionary)field.GetValue(registry)!;

        var total = 0;
        foreach (System.Collections.IList actions in byStage.Values)
            total += actions.Count;

        return total;
    }
}
