using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MintPlayer.Spark.Migrations;

/// <summary>Fluent surface for registering migrations.</summary>
public interface ISparkMigrationsBuilder
{
    /// <summary>
    /// Registers <typeparamref name="TMigration"/>, reading its version from its class name
    /// (<c>M_{yyyyMMddHHmm}_{Name}</c>, <see cref="SparkMigrationVersion"/>). The migration is resolved
    /// per run from a DI scope, so its <c>[Inject]</c> dependencies work.
    /// </summary>
    /// <exception cref="InvalidOperationException">The class name carries no version.</exception>
    ISparkMigrationsBuilder AddMigration<TMigration>() where TMigration : class, ISparkMigration;
}

internal sealed class SparkMigrationsBuilder(IServiceCollection services, SparkMigrationRegistry registry) : ISparkMigrationsBuilder
{
    public ISparkMigrationsBuilder AddMigration<TMigration>() where TMigration : class, ISparkMigration
    {
        // Resolved per-run from a DI scope so [Inject] dependencies work — never `new TMigration()`.
        services.TryAddScoped<TMigration>();

        registry.Add(new SparkMigrationDescriptor(
            MigrationType: typeof(TMigration),
            Version: SparkMigrationVersion.Of<TMigration>(),   // the class name's timestamp; refuses any other name
            Name: typeof(TMigration).Name,
            Description: TMigration.Description));

        return this;
    }
}
