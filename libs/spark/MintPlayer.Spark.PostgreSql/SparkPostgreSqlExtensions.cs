using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Relational;
using Npgsql;

namespace MintPlayer.Spark;

/// <summary><c>UsePostgreSql()</c>: PostgreSQL 17 or later as the application's store (#536, O8).</summary>
public static class SparkPostgreSqlExtensions
{
    /// <summary>Uses PostgreSQL, with the connection string from <c>ConnectionStrings:Spark</c> unless <paramref name="configure"/> sets one.</summary>
    [global::MintPlayer.Spark.Abstractions.Storage.SparkStoreProvider(global::MintPlayer.Spark.Abstractions.Storage.SparkStoreProviders.PostgreSql, global::MintPlayer.Spark.Abstractions.Storage.SparkStoreCapabilities.ChangeNotifications)]
    public static ISparkBuilder UsePostgreSql(this ISparkBuilder builder, Action<SparkRelationalOptions>? configure = null)
        => builder.UseRelational(new PostgreSqlSparkDialect(), configure);
}

/// <summary>PostgreSQL's dialect: the <c>"C"</c> key collation, <c>jsonb</c>, SQLSTATEs (#536 D2, D7, D30).</summary>
public sealed class PostgreSqlSparkDialect : SparkRelationalDialect
{
    public override string ProviderName => global::MintPlayer.Spark.Abstractions.Storage.SparkStoreProviders.PostgreSql;

    /// <summary>
    /// Npgsql, with <see cref="AdvisoryLockHistoryRepository"/> in place of the stock history repository, whose lock
    /// ends with each migration's transaction (npgsql/efcore.pg#4162; S19 scenario 1b). It goes when Npgsql ships the
    /// fix (npgsql/efcore.pg#4163).
    /// </summary>
    public override void UseProvider(DbContextOptionsBuilder builder, string connectionString, SparkMigrationsSettings migrations)
    {
        builder.UseNpgsql(connectionString, pg =>
        {
            if (migrations.Assembly is { } assembly)
                pg.MigrationsAssembly(assembly);
            pg.MigrationsHistoryTable(migrations.HistoryTable, migrations.HistorySchema);
        });
        builder.ReplaceService<Microsoft.EntityFrameworkCore.Migrations.IHistoryRepository, AdvisoryLockHistoryRepository>();
    }

    public override string EfProviderName => "Npgsql.EntityFrameworkCore.PostgreSQL";

    public override string KeyCollation => "C";

    /// <summary>
    /// A session advisory lock in the <c>postgres</c> maintenance database, keyed by a hash of
    /// <c>spark:migrations:{database}</c> passed as a parameter (S19 scenario 1c).
    /// </summary>
    public override async Task<IAsyncDisposable> AcquireMigrationGuardAsync(string connectionString, CancellationToken cancellationToken)
    {
        var target = new NpgsqlConnectionStringBuilder(connectionString);
        var resource = "spark:migrations:" + target.Database;
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres", Pooling = false }.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pg_advisory_lock(hashtextextended(@resource, 0))";
            command.Parameters.AddWithValue("resource", resource);
            command.CommandTimeout = 610;
            await command.ExecuteNonQueryAsync(cancellationToken);
            return new Guard(connection, resource);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private sealed class Guard(NpgsqlConnection connection, string resource) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT pg_advisory_unlock(hashtextextended(@resource, 0))";
                command.Parameters.AddWithValue("resource", resource);
                await command.ExecuteNonQueryAsync();
            }
            finally
            {
                // Closing an unpooled connection ends the session, which releases the lock in any case.
                await connection.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// A <see cref="DateTime"/> is <c>timestamp without time zone</c>, as <c>datetime2</c> is on SQL Server: Npgsql refuses
    /// to write a non-UTC value to <c>timestamptz</c>. A <see cref="DateTimeOffset"/> is <c>timestamptz</c>, written as
    /// its UTC instant (Npgsql refuses other offsets too); it reads back with offset 0, the same instant (D5).
    /// </summary>
    public override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<DateTime>().HaveColumnType("timestamp without time zone").HaveConversion<TicksDateTimeConverter>();
        builder.Properties<DateTimeOffset>().HaveConversion<UtcDateTimeOffsetConverter>();
    }

    /// <summary>The ticks as written, whatever the kind (Npgsql refuses a UTC value here too): what <c>datetime2</c> keeps.</summary>
    private sealed class TicksDateTimeConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>(
        value => DateTime.SpecifyKind(value, DateTimeKind.Unspecified), value => value);

    private sealed class UtcDateTimeOffsetConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, DateTimeOffset>(
        value => value.ToUniversalTime(), value => value);

    public override void JsonColumn(PropertyBuilder property) => property.HasColumnType("jsonb");

    public override string LockRowSql(string table, string column) => $"SELECT 1 FROM {table} WHERE {column} = {{0}} FOR UPDATE";

    /// <summary>D6: the writing transaction's id, which a trigger sets on every insert and update.</summary>
    public override string StampColumnType => "xid8";

    public override string? StampDefaultSql => "pg_current_xact_id()";

    /// <summary>The function every table's stamp trigger calls, in the framework schema (<c>SparkFramework0002</c>).</summary>
    public const string StampFunction = "spark.set_change_stamp";

    public override string? StampFrameworkSql => $$"""
        CREATE OR REPLACE FUNCTION {{StampFunction}}() RETURNS trigger LANGUAGE plpgsql AS $spark$
        BEGIN
            NEW."{{MintPlayer.Spark.Relational.Model.SparkDbContext.StampProperty}}" := pg_current_xact_id();
            RETURN NEW;
        END
        $spark$;
        """;

    public override string? StampTriggerSql(string table)
        => $"CREATE TRIGGER spark_change_stamp BEFORE INSERT OR UPDATE ON {table} FOR EACH ROW EXECUTE FUNCTION {StampFunction}();";

    /// <summary>
    /// Every transaction id below the snapshot's <c>xmin</c> has ended, and every open or later one is at or above it.
    /// </summary>
    public override string HorizonSql => "SELECT pg_snapshot_xmin(pg_current_snapshot())::text::bigint";

    /// <summary>PostgreSQL names the constraint and the column in fields of its own, never only in the message.</summary>
    public override SparkConstraintError? Classify(Exception databaseException)
    {
        if (databaseException is not PostgresException pg)
            return null;
        return pg.SqlState switch
        {
            PostgresErrorCodes.UniqueViolation => new(SparkConstraintKind.Unique, [pg.ConstraintName ?? ""]),
            PostgresErrorCodes.ForeignKeyViolation => new(SparkConstraintKind.ForeignKey, [pg.ConstraintName ?? ""]),
            PostgresErrorCodes.NotNullViolation => new(SparkConstraintKind.NotNull, [pg.ColumnName ?? ""]),
            PostgresErrorCodes.StringDataRightTruncation => new(SparkConstraintKind.Length, []),
            _ => null,
        };
    }
}
