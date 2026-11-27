using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using MintPlayer.Spark.Relational.Model;
using MintPlayer.Spark.Relational.Storage;

namespace MintPlayer.Spark.Relational.Schema;

/// <summary>
/// Schema built from the model rather than from migration files (#536 D33): for test databases and prototypes, never
/// for production, whose schema comes from the migrations synchronize writes. It goes through EF's migration
/// operations, as a migration does, so what <c>EnsureCreated</c> skips (views, M4d) is created too.
/// </summary>
public static class RelationalSchema
{
    /// <summary>
    /// Creates the database when it does not exist, applies the framework migrations, then creates every app table the
    /// model has.
    /// </summary>
    public static async Task CreateFromModelAsync(RelationalSparkStore store, CancellationToken cancellationToken = default)
    {
        await ApplyFrameworkAsync(store, cancellationToken);
        await using var app = store.NewContext();
        await ApplyModelDifferencesAsync(app, from: null, cancellationToken);
    }

    /// <summary>
    /// Brings the database from <paramref name="from"/>'s model to <paramref name="target"/>'s (the whole model when
    /// <paramref name="from"/> is <see langword="null"/>), as a migration between them would.
    /// </summary>
    public static Task ApplyDifferencesAsync(DbContext target, DbContext? from, CancellationToken cancellationToken = default)
        => ApplyModelDifferencesAsync(target, from?.GetService<IDesignTimeModel>().Model.GetRelationalModel(), cancellationToken);

    /// <summary>
    /// Brings the database from the relational model <paramref name="from"/> to <paramref name="target"/>'s, and returns
    /// the model it applied, for the next call (a test database several hosts use in turn, #536 D35).
    /// </summary>
    public static async Task<IRelationalModel> ApplyModelDifferencesAsync(DbContext target, IRelationalModel? from, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var model = target.GetService<IDesignTimeModel>().Model;
        var relational = model.GetRelationalModel();
        var operations = target.GetService<IMigrationsModelDiffer>().GetDifferences(from, relational);
        if (operations.Count == 0)
            return relational;

        var commands = target.GetService<IMigrationsSqlGenerator>().Generate(operations, model);
        await target.GetService<IMigrationCommandExecutor>().ExecuteNonQueryAsync(commands, target.GetService<IRelationalConnection>(), cancellationToken);
        return relational;
    }

    /// <summary>
    /// Creates the database when it does not exist and applies the framework migrations, without the app's tables.
    /// </summary>
    public static async Task ApplyFrameworkAsync(RelationalSparkStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        await using var framework = store.NewFrameworkContext();
        await EnsureDatabaseAsync(framework, cancellationToken);
        await framework.Database.MigrateAsync(cancellationToken);
    }

    /// <summary>Creates the context's database when it does not exist (call inside the migration guard: D33).</summary>
    internal static async Task EnsureDatabaseAsync(DbContext context, CancellationToken cancellationToken)
    {
        var creator = context.GetService<IRelationalDatabaseCreator>();
        if (!await creator.ExistsAsync(cancellationToken))
            await creator.CreateAsync(cancellationToken);
    }
}
