using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.CompareExchange;

namespace MintPlayer.Spark.Migrations;

/// <summary>
/// Applies pending migrations once, in version order, at startup. Idempotent across restarts via a
/// per-version marker document (<c>SparkMigrationRecords/{version}</c>) and safe across nodes via a
/// cluster-wide compare-exchange lock — the same primitive the cron scheduler uses.
/// </summary>
internal static class SparkMigrationRunner
{
    private const string LockKey = "spark/migrations/lock";
    private static readonly TimeSpan LockTtl = TimeSpan.FromMinutes(30);

    /// <summary>Synchronous entry point for the UseSpark startup hook. Blocks until migrations complete.</summary>
    public static void RunAtStartup(IServiceProvider services)
        => RunAsync(services, CancellationToken.None).GetAwaiter().GetResult();

    public static async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var registry = services.GetService<SparkMigrationRegistry>();
        var pending = registry?.Migrations ?? [];
        if (pending.Count == 0)
            return;

        var store = services.GetRequiredService<IDocumentStore>();
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger("MintPlayer.Spark.Migrations");

        // One node applies migrations; others skip and serve once it's done releasing the lock.
        if (!await TryAcquireLockAsync(store, cancellationToken))
        {
            logger?.LogInformation("Spark migrations: another instance holds the migration lock; skipping on this node.");
            return;
        }

        try
        {
            foreach (var migration in pending)
            {
                var markerId = MarkerId(migration.Version);

                using (var check = store.OpenAsyncSession())
                {
                    if (await check.Advanced.ExistsAsync(markerId, cancellationToken))
                        continue; // already applied
                }

                logger?.LogInformation("Applying Spark migration {Version} ({Name}){Description}.",
                    migration.Version, migration.Name,
                    string.IsNullOrEmpty(migration.Description) ? "" : $": {migration.Description}");

                // Resolve from a DI scope so the migration's [Inject] dependencies work.
                using (var scope = services.CreateScope())
                {
                    var instance = (ISparkMigration)scope.ServiceProvider.GetRequiredService(migration.MigrationType);
                    await instance.UpAsync(cancellationToken);
                }

                // Mark as applied only after Up succeeded — a throw above aborts startup and leaves
                // the migration unmarked so it retries next time.
                using (var save = store.OpenAsyncSession())
                {
                    await save.StoreAsync(new SparkMigrationRecord
                    {
                        Version = migration.Version,
                        Name = migration.Name,
                        AppliedOnUtc = DateTimeOffset.UtcNow,
                    }, markerId, cancellationToken);
                    await save.SaveChangesAsync(cancellationToken);
                }

                logger?.LogInformation("Spark migration {Version} ({Name}) applied.", migration.Version, migration.Name);
            }
        }
        finally
        {
            await ReleaseLockAsync(store, cancellationToken);
        }
    }

    internal static string MarkerId(long version) => $"SparkMigrationRecords/{version}";

    private static async Task<bool> TryAcquireLockAsync(IDocumentStore store, CancellationToken ct)
    {
        var expiry = DateTime.UtcNow.Add(LockTtl);

        // index 0 means "only succeeds if the key does not exist yet".
        var put = await store.Operations.SendAsync(
            new PutCompareExchangeValueOperation<DateTime>(LockKey, expiry, 0), token: ct);
        if (put.Successful)
            return true;

        // Held — take it over only if the existing lease has expired (a crashed run).
        var current = await store.Operations.SendAsync(
            new GetCompareExchangeValueOperation<DateTime>(LockKey), token: ct);
        if (current is not null && current.Value < DateTime.UtcNow)
        {
            var takeover = await store.Operations.SendAsync(
                new PutCompareExchangeValueOperation<DateTime>(LockKey, expiry, current.Index), token: ct);
            return takeover.Successful;
        }

        return false;
    }

    private static async Task ReleaseLockAsync(IDocumentStore store, CancellationToken ct)
    {
        var current = await store.Operations.SendAsync(
            new GetCompareExchangeValueOperation<DateTime>(LockKey), token: ct);
        if (current is not null)
            await store.Operations.SendAsync(
                new DeleteCompareExchangeValueOperation<DateTime>(LockKey, current.Index), token: ct);
    }
}
