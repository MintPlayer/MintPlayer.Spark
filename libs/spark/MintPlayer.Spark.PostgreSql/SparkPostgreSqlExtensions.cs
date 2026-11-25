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
