namespace MintPlayer.Spark.Abstractions.Storage;

/// <summary>
/// A store's schema migrations (#536 D33, D34). Only a provider with a schema registers one (SQL Server, PostgreSQL:
/// Spark's framework tables, then the app's EF migrations); RavenDB and the in-memory store register none, and core
/// treats that as nothing to migrate. Core owns when it runs: at startup by default, refused at startup while
/// <c>Spark:Database:ApplyMigrations=false</c> leaves migrations pending, as a separate step with
/// <c>--spark-migrate</c>, or as a script with <c>--spark-migration-script</c>.
/// </summary>
public interface ISparkSchemaMigrator
{
    /// <summary>The migrations the database has not applied yet, oldest first.</summary>
    Task<IReadOnlyList<string>> GetPendingMigrationsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the database if needed and applies every pending migration. The provider serializes this across
    /// instances itself, the database's creation included (S19: the database's own migration lock does not cover it).
    /// </summary>
    Task ApplyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes one idempotent SQL script for a DBA: every migration after <paramref name="fromMigration"/> up to
    /// <paramref name="toMigration"/> (all of them when both are <see langword="null"/>), checked against the
    /// history tables so it can run on any database.
    /// </summary>
    Task WriteScriptAsync(TextWriter output, string? fromMigration, string? toMigration, CancellationToken cancellationToken = default);
}

/// <summary>
/// The data migrations (<c>ISparkMigration</c>), as <c>MintPlayer.Spark.Migrations</c> registers them, for
/// <c>--spark-migrate</c> to run after the schema. At startup they run in their own slot of <c>UseSpark</c>.
/// </summary>
public interface ISparkDataMigrations
{
    /// <summary>Applies the pending data migrations once, across every instance.</summary>
    Task RunAsync(CancellationToken cancellationToken);
}
