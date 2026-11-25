using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Relational;

namespace MintPlayer.Spark;

/// <summary><c>UseSqlServer()</c>: SQL Server 2022 or later as the application's store (#536).</summary>
public static class SparkSqlServerExtensions
{
    /// <summary>Uses SQL Server, with the connection string from <c>ConnectionStrings:Spark</c> unless <paramref name="configure"/> sets one.</summary>
    [global::MintPlayer.Spark.Abstractions.Storage.SparkStoreProvider(global::MintPlayer.Spark.Abstractions.Storage.SparkStoreProviders.SqlServer, global::MintPlayer.Spark.Abstractions.Storage.SparkStoreCapabilities.None)]
    public static ISparkBuilder UseSqlServer(this ISparkBuilder builder, Action<SparkRelationalOptions>? configure = null)
        => builder.UseRelational(new SqlServerSparkDialect(), configure);
}

/// <summary>SQL Server's dialect: binary key collation, error numbers (#536 D2, D7, D30).</summary>
public sealed partial class SqlServerSparkDialect : SparkRelationalDialect
{
    public override string ProviderName => global::MintPlayer.Spark.Abstractions.Storage.SparkStoreProviders.SqlServer;

    public override void UseProvider(DbContextOptionsBuilder builder, string connectionString, SparkMigrationsSettings migrations)
        => builder.UseSqlServer(connectionString, sql =>
        {
            if (migrations.Assembly is { } assembly)
                sql.MigrationsAssembly(assembly);
            sql.MigrationsHistoryTable(migrations.HistoryTable, migrations.HistorySchema);
        });

    public override string EfProviderName => "Microsoft.EntityFrameworkCore.SqlServer";

    public override string KeyCollation => "Latin1_General_100_BIN2";

    /// <summary>
    /// <c>sp_getapplock</c> owned by the session, in <c>master</c>: it exists before the target database does, and a
    /// lock taken in the target database could not cover its creation (S19 scenario 1c).
    /// </summary>
    public override async Task<IAsyncDisposable> AcquireMigrationGuardAsync(string connectionString, CancellationToken cancellationToken)
    {
        var target = new SqlConnectionStringBuilder(connectionString);
        var resource = "spark:migrations:" + target.InitialCatalog;
        var connection = new SqlConnection(new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master", Pooling = false }.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "DECLARE @r int; EXEC @r = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 600000; SELECT @r;";
            command.Parameters.AddWithValue("@resource", resource);
            command.CommandTimeout = 610;
            var result = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
            if (result < 0)
                throw new InvalidOperationException($"Spark could not take the migration lock '{resource}' (sp_getapplock returned {result}).");
            return new Guard(connection, resource);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private sealed class Guard(SqlConnection connection, string resource) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "EXEC sp_releaseapplock @Resource = @resource, @LockOwner = 'Session';";
                command.Parameters.AddWithValue("@resource", resource);
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
    /// <c>decimal(38, 18)</c>, not EF's <c>decimal(18, 2)</c>, which rounds a document's <c>1234.5678</c> to <c>1234.57</c>:
    /// 20 integer and 18 fractional digits.
    /// </summary>
    public override void ConfigureConventions(ModelConfigurationBuilder builder)
        => builder.Properties<decimal>().HavePrecision(38, 18);

    /// <summary>
    /// SQL Server names the constraint, index or column only inside its message, which is localized by the session's
    /// language (S4: German drops the quotes). Every identifier-like word is offered; the model picks the one it knows.
    /// </summary>
    public override SparkConstraintError? Classify(Exception databaseException)
    {
        if (databaseException is not SqlException sql)
            return null;
        SparkConstraintKind? kind = sql.Number switch
        {
            2601 or 2627 => SparkConstraintKind.Unique,
            547 => SparkConstraintKind.ForeignKey,
            515 => SparkConstraintKind.NotNull,
            2628 or 8152 => SparkConstraintKind.Length,
            _ => null,
        };
        return kind is { } k ? new SparkConstraintError(k, [.. Word().Matches(sql.Message).Select(m => m.Value)]) : null;
    }

    public override string LockRowSql(string table, string column) => $"SELECT 1 FROM {table} WITH (UPDLOCK, ROWLOCK) WHERE {column} = {{0}}";

    [GeneratedRegex("[A-Za-z0-9_]+")]
    private static partial Regex Word();
}
