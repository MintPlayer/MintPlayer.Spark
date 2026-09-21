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

    /// <param name="lockWaitBudget">
    /// How long to wait for a lock another instance holds before failing startup. Defaults to
    /// <see cref="LockWaitBudget"/>; tests pass something short, because the default is
    /// deliberately longer than the lock's own TTL and waiting it out is not a test.
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

        var store = services.GetRequiredService<IDocumentStore>();
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger("MintPlayer.Spark.Migrations");

        // One node applies migrations; the others WAIT for it rather than serving past it.
        var lockIndex = await AcquireOrWaitAsync(
            store, logger, pending, lockWaitBudget ?? LockWaitBudget, cancellationToken);
        if (lockIndex is null)
            return; // everything pending was applied by whoever held the lock.

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
            await ReleaseLockAsync(store, lockIndex.Value, cancellationToken);
        }
    }

    internal static string MarkerId(long version) => $"SparkMigrationRecords/{version}";

    /// <summary>
    /// How long to keep waiting for a lock somebody else holds before giving up and failing
    /// startup. Comfortably longer than <see cref="LockTtl"/>, so a lock left behind by a killed
    /// process is always taken over rather than waited out.
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
    /// every restart for the next <see cref="LockTtl"/> logged "another instance holds the
    /// migration lock; skipping" and then served a <b>half-migrated database</b>. Nothing was
    /// logged as wrong, because from the app's point of view nothing was.
    /// </para>
    /// <para>
    /// A thrown migration releases the lock in its <c>finally</c> and retries on the next start,
    /// which is what made this look safe. A <em>killed</em> process runs no <c>finally</c> —
    /// OOM-killer, <c>docker kill</c>, a host reboot or a deploy timeout all take this path, and
    /// those are exactly the conditions a long migration invites.
    /// </para>
    /// <para>
    /// Waiting is correct for the legitimate case too: a second instance starting alongside the
    /// migrating one should come up <em>after</em> the migration, not race it.
    /// </para>
    /// </remarks>
    private static async Task<long?> AcquireOrWaitAsync(
        IDocumentStore store,
        ILogger? logger,
        IReadOnlyCollection<SparkMigrationDescriptor> pending,
        TimeSpan waitBudget,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.Add(waitBudget);
        var announced = false;

        while (true)
        {
            var acquired = await TryAcquireLockAsync(store, cancellationToken);
            if (acquired is not null)
                return acquired;

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
        IDocumentStore store,
        IReadOnlyCollection<SparkMigrationDescriptor> pending,
        CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        foreach (var migration in pending)
        {
            if (!await session.Advanced.ExistsAsync(MarkerId(migration.Version), cancellationToken))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Takes the migration lock, returning the compare-exchange index our claim landed at, or null
    /// if another instance holds it. The index is the proof of ownership that
    /// <see cref="ReleaseLockAsync"/> needs.
    /// </summary>
    private static async Task<long?> TryAcquireLockAsync(IDocumentStore store, CancellationToken ct)
    {
        var expiry = DateTime.UtcNow.Add(LockTtl);

        // index 0 means "only succeeds if the key does not exist yet".
        var put = await store.Operations.SendAsync(
            new PutCompareExchangeValueOperation<DateTime>(LockKey, expiry, 0), token: ct);
        if (put.Successful)
            return put.Index;

        // Held — take it over only if the existing lease has expired (a crashed run).
        var current = await store.Operations.SendAsync(
            new GetCompareExchangeValueOperation<DateTime>(LockKey), token: ct);
        if (current is not null && current.Value < DateTime.UtcNow)
        {
            var takeover = await store.Operations.SendAsync(
                new PutCompareExchangeValueOperation<DateTime>(LockKey, expiry, current.Index), token: ct);
            return takeover.Successful ? takeover.Index : null;
        }

        return null;
    }

    /// <summary>
    /// Releases the lock only if it is still the one we took.
    /// </summary>
    /// <remarks>
    /// This used to read the current value and delete it unconditionally at whatever index it found,
    /// which meant it could delete a lock belonging to <i>another</i> instance: if our run outlived
    /// the 30-minute TTL, another node legitimately took the lock over, and our release then
    /// deleted theirs — leaving a live migration running with no lock and a third node free to
    /// start its own. Deleting at the index our own claim produced makes the delete fail harmlessly
    /// in exactly that case, because a takeover changes the index.
    /// <para>
    /// The remaining hazard is unchanged and deliberately out of scope here: the lock is not
    /// renewed, so a backfill longer than <see cref="LockTtl"/> can still be taken over
    /// mid-flight. That only matters with more than one instance, which is not how this is
    /// deployed — see the standing constraint in
    /// <c>docs/decisions_messaging_and_project_automation.md</c> §8.
    /// </para>
    /// </remarks>
    private static async Task ReleaseLockAsync(IDocumentStore store, long acquiredIndex, CancellationToken ct)
        => await store.Operations.SendAsync(
            new DeleteCompareExchangeValueOperation<DateTime>(LockKey, acquiredIndex), token: ct);
}
