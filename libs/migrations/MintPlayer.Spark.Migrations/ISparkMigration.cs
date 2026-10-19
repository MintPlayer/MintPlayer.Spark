namespace MintPlayer.Spark.Migrations;

/// <summary>
/// A one-time database migration. Implement this on a non-abstract class named
/// <c>M_{yyyyMMddHHmm}_{Name}</c> and Spark will run it once, in version order, at application
/// startup — after indexes are created and before the app serves requests. Each migration runs in
/// its own DI scope, so it can take dependencies via <c>[Inject]</c>
/// (e.g. <see cref="Raven.Client.Documents.Session.IAsyncDocumentSession"/>), exactly like a cron job.
/// </summary>
/// <remarks>
/// Discovery is by interface — no attribute or base class. The source generator finds every
/// non-abstract implementer and wires them into the generated <c>spark.AddSparkDiscovered()</c>.
/// The version is the class name's timestamp (<see cref="SparkMigrationVersion"/>): it decides the run
/// order and the marker-document key, and registration refuses a class not named that way.
/// A migration is applied at most once per database (tracked by a per-version marker document)
/// and is safe to run from multiple nodes at once (a cluster-wide compare-exchange lock serialises
/// the run).
/// </remarks>
public interface ISparkMigration
{
    /// <summary>Optional human-readable description, used in startup logs.</summary>
    static virtual string? Description => null;

    /// <summary>
    /// Applies the migration. Throwing aborts startup (fail-fast) and leaves the migration
    /// unmarked, so it is retried on the next start. Call <c>SaveChangesAsync</c> on any session
    /// you mutate; the framework writes the version marker only after this returns successfully.
    /// </summary>
    Task UpAsync(CancellationToken cancellationToken);
}
