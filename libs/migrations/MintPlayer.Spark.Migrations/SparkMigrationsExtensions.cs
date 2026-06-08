using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Migrations;

public static class SparkMigrationsExtensions
{
    /// <summary>
    /// Registers the Spark migration runner and the supplied migrations. Pending migrations run
    /// once, in version order, during <c>UseSpark()</c> — after indexes are created and before the
    /// app serves requests. Safe to call more than once; the registry and startup hook are wired a
    /// single time and each call may add more migrations.
    /// </summary>
    /// <remarks>
    /// Prefer the generated <c>spark.AddMigrations()</c> (no argument), which auto-discovers every
    /// <see cref="ISparkMigration"/> in the project. This overload is the manual escape hatch.
    /// </remarks>
    public static ISparkBuilder AddMigrations(this ISparkBuilder builder, Action<ISparkMigrationsBuilder> configure)
    {
        var registry = GetOrAddInfrastructure(builder);
        configure(new SparkMigrationsBuilder(builder.Services, registry));
        return builder;
    }

    private static readonly object infrastructureGate = new();

    private static SparkMigrationRegistry GetOrAddInfrastructure(ISparkBuilder builder)
    {
        lock (infrastructureGate)
        {
            var existing = builder.Services
                .FirstOrDefault(d => d.ServiceType == typeof(SparkMigrationRegistry))?
                .ImplementationInstance as SparkMigrationRegistry;

            if (existing is not null)
                return existing;

            var registry = new SparkMigrationRegistry();
            builder.Services.AddSingleton(registry);

            // Run pending migrations once at startup. Registry middleware actions execute during
            // UseSpark, after CreateSparkIndexes and before the request pipeline serves traffic —
            // the same "do once at startup against the store" slot index creation uses.
            builder.Registry.AddMiddleware(app => SparkMigrationRunner.RunAtStartup(app.ApplicationServices));

            return registry;
        }
    }
}
