using Microsoft.Extensions.Configuration;
using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.Services;

/// <summary>
/// The schema step of startup (#536 D33): first in <c>UseSpark</c>, before anything reads a table. With no
/// <see cref="ISparkSchemaMigrator"/> (RavenDB, in-memory) there is nothing to do.
/// </summary>
internal static class SparkSchemaStartup
{
    internal const string ApplyMigrationsKey = "Spark:Database:ApplyMigrations";
    internal const string MigrateFlag = "--spark-migrate";
    internal const string ScriptFlag = "--spark-migration-script";

    internal static async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        if (services.GetService<ISparkSchemaMigrator>() is not { } migrator)
            return;

        if (services.GetService<IConfiguration>()?.GetValue(ApplyMigrationsKey, true) ?? true)
        {
            await migrator.ApplyAsync(cancellationToken);
            return;
        }

        // A deployment that migrates separately runs with a login without DDL rights: refuse rather than run against
        // an older schema.
        var pending = await migrator.GetPendingMigrationsAsync(cancellationToken);
        if (pending.Count > 0)
            throw new InvalidOperationException(
                $"The database has {pending.Count} pending migration(s) ({string.Join(", ", pending)}) and "
                + $"{ApplyMigrationsKey} is false. Apply them first: run the app with {MigrateFlag}, or give a DBA the "
                + $"script from {ScriptFlag} <file>.");
    }
}
