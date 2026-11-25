using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.Migrations;

/// <summary>
/// Applies pending migrations once, in version order, at startup. Idempotent across restarts via a per-version marker
/// document (<c>sparkmigrationrecords/{version}</c>) and safe across instances via a lease on the store
/// (<see cref="ISparkStoreLock"/>, #536 S15), so it runs on every store provider.
/// </summary>
internal static class SparkMigrationRunner
{
    /// <summary>The key the migration runner's lock has always had, so a lock the previous version left is respected.</summary>
    internal const string LockKey = "spark/migrations/lock";

    /// <summary>
    /// The lease is renewed while the migrations run, so this is how soon a <em>killed</em> runner's lock frees, not a
    /// limit on how long a migration may take.
    /// </summary>
    internal static readonly TimeSpan LockTtl = TimeSpan.FromMinutes(2);

    /// <summary>Synchronous entry point for the UseSpark startup hook. Blocks until migrations complete.</summary>
    public static void RunAtStartup(IServiceProvider services)
        => RunAsync(services, CancellationToken.None).GetAwaiter().GetResult();

    /// <param name="lockWaitBudget">
    /// How long to wait for a lock another instance holds before failing startup. Defaults to
    /// <see cref="LockWaitBudget"/>; tests pass something short.
    /// </param>
    public static async Task RunAsync(
        IServiceProvider services,
        CancellationToken cancellationToken,
        TimeSpan? lockWaitBudget = null)
    {
        var registry = services.GetService<SparkMigrationRegistry>();
        var pending = registry?.Migrations ?? [];
        if (pending.Count == 0)
            return;

        var store = services.GetRequiredService<ISparkStore>();
        var locks = services.GetRequiredService<ISparkStoreLock>();
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger("MintPlayer.Spark.Migrations");

        // One instance applies migrations; the others WAIT for it rather than serving past it.
        await using var lease = await AcquireOrWaitAsync(
            store, locks, logger, pending, lockWaitBudget ?? LockWaitBudget, cancellationToken);
        if (lease is null)
            return; // everything pending was applied by whoever held the lock.

        // A lost lease (another instance took it over) stops the run rather than let two runners apply the same migration.
        using var guarded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.Lost);
        var token = guarded.Token;

        foreach (var migration in pending)
        {
            var markerId = MarkerId(migration.Version);

            using (var check = store.OpenSession())
            {
                if (await check.ExistsAsync(markerId, token))
                    continue; // already applied
            }

            logger?.LogInformation("Applying Spark migration {Version} ({Name}){Description}.",
                migration.Version, migration.Name,
                string.IsNullOrEmpty(migration.Description) ? "" : $": {migration.Description}");

            // Resolve from a DI scope so the migration's [Inject] dependencies work.
            using (var scope = services.CreateScope())
            {
                var instance = (ISparkMigration)scope.ServiceProvider.GetRequiredService(migration.MigrationType);
                await instance.UpAsync(token);
            }

            // Mark as applied only after Up succeeded — a throw above aborts startup and leaves
            // the migration unmarked so it retries next time.
            using (var save = store.OpenSession())
            {
                await save.StoreAsync(new SparkMigrationRecord
                {
                    Id = markerId,
                    Version = migration.Version,
                    Name = migration.Name,
                    AppliedOnUtc = DateTimeOffset.UtcNow,
                }, token);
                await save.SaveChangesAsync(token);
            }

            logger?.LogInformation("Spark migration {Version} ({Name}) applied.", migration.Version, migration.Name);
        }
    }

    internal static Id<SparkMigrationRecord> MarkerId(long version) => Id<SparkMigrationRecord>.FromTrusted($"sparkmigrationrecords/{version}");

    /// <summary>
    /// How long to keep waiting for a lock somebody else holds before giving up and failing startup. Longer than the
    /// 30 minutes the lock lasted before #536 (it was not renewed then), so a lock the previous version left behind is
    /// always taken over rather than waited out.
    /// </summary>
    private static readonly TimeSpan LockWaitBudget = TimeSpan.FromMinutes(35);

    private static readonly TimeSpan LockPollInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Acquires the lock, or waits until the holder has finished and every pending migration is
    /// marked applied. Returns null when there is nothing left to do.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>This used to skip and serve, and that was a data-correctness bug.</b> A node that
    /// cannot take the lock has, by definition, pending migrations that have not been applied
    /// <em>yet</em> — so serving is serving un-migrated data. Measured 2026-09-21 by killing a
    /// container in the middle of a 224,000-document re-key: the lock survives the process, and
    /// every restart for the next 30 minutes logged "another instance holds the
    /// migration lock; skipping" and then served a <b>half-migrated database</b>. Nothing was
    /// logged as wrong, because from the app's point of view nothing was.
    /// </para>
    /// <para>
    /// A thrown migration releases the lock when the lease is disposed and retries on the next start,
    /// which is what made this look safe. A <em>killed</em> process releases nothing —
    /// OOM-killer, <c>docker kill</c>, a host reboot or a deploy timeout all take this path, and
    /// those are exactly the conditions a long migration invites. Its lease stops being renewed and
    /// frees after <see cref="LockTtl"/>.
    /// </para>
    /// <para>
    /// Waiting is correct for the legitimate case too: a second instance starting alongside the
    /// migrating one should come up <em>after</em> the migration, not race it.
    /// </para>
    /// </remarks>
    private static async Task<ISparkLease?> AcquireOrWaitAsync(
        ISparkStore store,
        ISparkStoreLock locks,
        ILogger? logger,
        IReadOnlyCollection<SparkMigrationDescriptor> pending,
        TimeSpan waitBudget,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.Add(waitBudget);
        var announced = false;

        while (true)
        {
            if (await locks.TryAcquireAsync(LockKey, LockTtl, cancellationToken) is { } lease)
                return lease;

            // The holder may have finished everything while we were waiting, in which case there
            // is nothing to migrate and serving is safe.
            if (await AllAppliedAsync(store, pending, cancellationToken))
            {
                logger?.LogInformation(
                    "Spark migrations: another instance applied them; continuing startup.");
                return null;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new InvalidOperationException(
                    $"Spark migrations: the migration lock has been held by another instance for over "
                    + $"{waitBudget.TotalMinutes:0.#} minutes and migrations are still pending. "
                    + "Refusing to start, because serving now would serve un-migrated data. If no "
                    + $"other instance is running, the lock is stale and clears itself {LockTtl.TotalMinutes:0} "
                    + "minutes after the process that took it stopped.");
            }

            if (!announced)
            {
                logger?.LogWarning(
                    "Spark migrations: another instance holds the migration lock and migrations are "
                    + "pending. Waiting rather than serving un-migrated data.");
                announced = true;
            }

            var poll = waitBudget < LockPollInterval ? waitBudget : LockPollInterval;
            await Task.Delay(poll, cancellationToken);
        }
    }

    private static async Task<bool> AllAppliedAsync(
        ISparkStore store,
        IReadOnlyCollection<SparkMigrationDescriptor> pending,
        CancellationToken cancellationToken)
    {
        using var session = store.OpenSession();
        using var uncapped = session.IgnoreMaxRequests(); // one existence check per migration, however many there are
        foreach (var migration in pending)
        {
            if (!await session.ExistsAsync(MarkerId(migration.Version), cancellationToken))
                return false;
        }

        return true;
    }
}
