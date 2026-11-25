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
    [global::MintPlayer.Spark.Abstractions.Storage.SparkStoreProvider(global::MintPlayer.Spark.Abstractions.Storage.SparkStoreProviders.PostgreSql, global::MintPlayer.Spark.Abstractions.Storage.SparkStoreCapabilities.None)]
    public static ISparkBuilder UsePostgreSql(this ISparkBuilder builder, Action<SparkRelationalOptions>? configure = null)
        => builder.UseRelational(new PostgreSqlSparkDialect(), configure);
}

/// <summary>PostgreSQL's dialect: the <c>"C"</c> key collation, <c>jsonb</c>, SQLSTATEs (#536 D2, D7, D30).</summary>
public sealed class PostgreSqlSparkDialect : SparkRelationalDialect
{
    public override string ProviderName => global::MintPlayer.Spark.Abstractions.Storage.SparkStoreProviders.PostgreSql;

    public override void UseProvider(DbContextOptionsBuilder builder, string connectionString) => builder.UseNpgsql(connectionString);

    public override string KeyCollation => "C";

    public override void JsonColumn(PropertyBuilder property) => property.HasColumnType("jsonb");

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
