using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Relational.Storage;

namespace MintPlayer.Spark.Relational.Schema;

/// <summary>
/// The SQL providers' <see cref="ISparkSchemaMigrator"/> (#536 D33): the framework schema first (its own history
/// table), then the app's EF migrations, under Spark's guard in the server's maintenance database, which also covers
/// creating the database (S19).
/// </summary>
public sealed class RelationalSchemaMigrator : ISparkSchemaMigrator
{
    private readonly RelationalSparkStore store;

    public RelationalSchemaMigrator(RelationalSparkStore store) => this.store = store;

    public async Task<IReadOnlyList<string>> GetPendingMigrationsAsync(CancellationToken cancellationToken = default)
    {
        await using var framework = store.NewFrameworkContext();
        await using var app = store.NewContext();
        if (!await framework.GetService<IRelationalDatabaseCreator>().ExistsAsync(cancellationToken))
            return [.. framework.Database.GetMigrations(), .. app.Database.GetMigrations()];
        return [.. await framework.Database.GetPendingMigrationsAsync(cancellationToken), .. await app.Database.GetPendingMigrationsAsync(cancellationToken)];
    }

    public async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        await using var guard = await store.Setup.Dialect.AcquireMigrationGuardAsync(store.ConnectionString, cancellationToken);

        await using (var framework = store.NewFrameworkContext())
        {
            await RelationalSchema.EnsureDatabaseAsync(framework, cancellationToken);
            await framework.Database.MigrateAsync(cancellationToken);
        }

        // EF refuses too (PendingModelChangesWarning), but names neither the fix nor Spark's command.
        await using (var check = store.NewContext())
        {
            if (check.Database.HasPendingModelChanges())
                throw new InvalidOperationException(
                    "The Spark model has changes that no migration covers"
                    + (store.MigrationsAssembly is { } assembly ? $" in {assembly}" : " (the store has no migrations assembly)")
                    + $" ({PendingChanges(check)})"
                    + ". Run the app with --spark-synchronize-model on a development machine, review the migration it writes, and commit it.");
        }

        // An app with no tables of its own and no migrations has nothing to apply (EF calls that MigrationsNotFound).
        await using var app = store.NewContext();
        if (app.Database.GetMigrations().Any())
            await app.Database.MigrateAsync(cancellationToken);
    }

    public async Task WriteScriptAsync(TextWriter output, string? fromMigration, string? toMigration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        await using var framework = store.NewFrameworkContext();
        await using var app = store.NewContext();

        // The framework schema is scripted whole: the script is idempotent, and the app's migrations rely on it.
        await output.WriteLineAsync("-- Spark framework schema (history: " + SparkFrameworkSchemaHistory + ")");
        await output.WriteLineAsync(framework.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent));
        await output.WriteLineAsync("-- The app's migrations (history: " + RelationalSparkStore.AppHistoryTable + ")");
        await output.WriteLineAsync(app.GetService<IMigrator>().GenerateScript(fromMigration, toMigration, MigrationsSqlGenerationOptions.Idempotent));
    }

    /// <summary>
    /// What the model has that the last migration's snapshot lacks, as EF's own check compares them, so the error names
    /// the table and column instead of leaving the operator to diff the model by hand.
    /// </summary>
    private static string PendingChanges(DbContext context)
    {
        // A new instance of the snapshot class: HasPendingModelChanges finalized the shared one (dotnet/efcore#36016).
        var shared = context.GetService<IMigrationsAssembly>().ModelSnapshot;
        IModel? snapshot = shared is null ? null : ((ModelSnapshot)Activator.CreateInstance(shared.GetType())!).Model;
        if (snapshot is IMutableModel mutable)
            snapshot = mutable.FinalizeModel();
        if (snapshot is not null)
            snapshot = context.GetService<IModelRuntimeInitializer>().Initialize(snapshot);
        var operations = context.GetService<IMigrationsModelDiffer>().GetDifferences(
            snapshot?.GetRelationalModel(), context.GetService<IDesignTimeModel>().Model.GetRelationalModel());
        var described = operations.Take(10).Select(SparkMigrationOperations.Describe).ToList();
        if (operations.Count > described.Count)
            described.Add($"{operations.Count - described.Count} more");
        return described.Count == 0 ? "no operation; the snapshot differs only in annotations" : string.Join(", ", described);
    }

    private static string SparkFrameworkSchemaHistory => Model.SparkFrameworkSchema.Schema + "." + Model.SparkFrameworkSchema.HistoryTable;
}
