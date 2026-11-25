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

    public override void UseProvider(DbContextOptionsBuilder builder, string connectionString) => builder.UseSqlServer(connectionString);

    public override string KeyCollation => "Latin1_General_100_BIN2";

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
