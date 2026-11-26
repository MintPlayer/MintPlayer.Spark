using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using MintPlayer.Spark.Relational.Model;
using MintPlayer.Spark.Relational.Schema;
using MintPlayer.Spark.Relational.Storage;
using MintPlayer.Spark.Relational.Tests._Infrastructure;

namespace MintPlayer.Spark.Relational.Tests.Schema;

public class MigPerson
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// The SQL providers' schema migrator (#536 M4c, D33): the framework schema under Spark's guard, the refusal when the
/// app's model has changes no migration covers, and the idempotent script.
/// </summary>
public abstract partial class RelationalSchemaMigratorTests : IAsyncLifetime
{
    private static readonly string[] FrameworkMigrations =
        ["20261125000001_SparkFramework0001", "20261125000002_SparkFramework0002", "20261126000003_SparkFramework0003", "20261126000004_SparkFramework0004"];

    private readonly List<RelationalSparkStore> created = [];

    protected abstract RelationalTestServer? Server { get; }

    private RelationalTestServer RequireServer => Server ?? throw new SkipException("No server configured for this provider.");

    private RelationalSparkStore NewStore(params Type[] types)
    {
        var store = RelationalTestDatabase.Open(RequireServer, types, null, "spark_mig_" + Guid.NewGuid().ToString("N")[..12]);
        created.Add(store);
        return store;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var store in created)
        {
            await using var db = store.NewFrameworkContext();
            await db.Database.EnsureDeletedAsync();
        }
    }

    [SkippableFact]
    public async Task A_database_that_does_not_exist_has_the_framework_migration_pending()
    {
        var migrator = new RelationalSchemaMigrator(NewStore());

        (await migrator.GetPendingMigrationsAsync()).Should().Equal(FrameworkMigrations);
    }

    [SkippableFact]
    public async Task Three_instances_create_and_migrate_the_database_once()
    {
        var store = NewStore();

        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() => new RelationalSchemaMigrator(store).ApplyAsync())));

        (await new RelationalSchemaMigrator(store).GetPendingMigrationsAsync()).Should().BeEmpty();
        await using var framework = store.NewFrameworkContext();
        (await framework.Database.GetAppliedMigrationsAsync()).Should().Equal(FrameworkMigrations);
    }

    [SkippableFact]
    public async Task A_model_no_migration_covers_is_refused_with_the_command_that_fixes_it()
    {
        var migrator = new RelationalSchemaMigrator(NewStore(typeof(MigPerson)));

        var refusal = await Record.ExceptionAsync(() => migrator.ApplyAsync());

        refusal.Should().BeOfType<InvalidOperationException>();
        refusal!.Message.Should().Contain("--spark-synchronize-model");
    }

    [SkippableFact]
    public async Task The_script_applies_twice_and_leaves_nothing_pending()
    {
        var store = NewStore();
        var writer = new StringWriter();
        await new RelationalSchemaMigrator(store).WriteScriptAsync(writer, null, null);
        await using (var framework = store.NewFrameworkContext())
            await framework.GetService<IRelationalDatabaseCreator>().CreateAsync();

        await RunScriptAsync(store, writer.ToString());
        await RunScriptAsync(store, writer.ToString());

        (await new RelationalSchemaMigrator(store).GetPendingMigrationsAsync()).Should().BeEmpty();
    }

    [SkippableFact]
    public async Task The_framework_migrations_build_the_framework_model()
    {
        // The framework migrations are hand-written, with no snapshot: compare what they create with what the
        // framework model's own migration operations create.
        var migrated = NewStore();
        await new RelationalSchemaMigrator(migrated).ApplyAsync();

        var fromModel = NewStore();
        await using (var framework = fromModel.NewFrameworkContext())
        {
            await framework.GetService<IRelationalDatabaseCreator>().CreateAsync();
            await RelationalSchema.ApplyDifferencesAsync(framework, from: null);
        }

        var expected = await ColumnsAsync(fromModel);
        expected.Should().NotBeEmpty();
        (await ColumnsAsync(migrated)).Should().Equal(expected);
    }

    private static async Task<List<string>> ColumnsAsync(RelationalSparkStore store)
    {
        await using var db = store.NewFrameworkContext();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT table_name, column_name, data_type, character_maximum_length, is_nullable, collation_name
            FROM information_schema.columns
            WHERE table_schema = 'spark' AND table_name NOT LIKE '\_\_%' ESCAPE '\'
            ORDER BY table_name, column_name
            """;
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "" : Convert.ToString(reader.GetValue(i)))));
        return rows;
    }

    /// <summary>What a DBA does with the script: <c>sqlcmd</c> splits SQL Server's on <c>GO</c>; PostgreSQL runs it whole.</summary>
    private async Task RunScriptAsync(RelationalSparkStore store, string script)
    {
        await using var db = store.NewFrameworkContext();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        var batches = RequireServer.Dialect is SqlServerSparkDialect ? Go().Split(script) : [script];
        foreach (var batch in batches.Where(b => !string.IsNullOrWhiteSpace(b)))
        {
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = batch;
            await command.ExecuteNonQueryAsync();
        }
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex Go();
}

public sealed class SqlServerSchemaMigratorTests : RelationalSchemaMigratorTests
{
    protected override RelationalTestServer? Server => RelationalTestServer.SqlServer;
}

public sealed class PostgreSqlSchemaMigratorTests : RelationalSchemaMigratorTests
{
    protected override RelationalTestServer? Server => RelationalTestServer.PostgreSql;
}
