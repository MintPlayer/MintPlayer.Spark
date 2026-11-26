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
    [global::MintPlayer.Spark.Abstractions.Storage.SparkStoreProvider(global::MintPlayer.Spark.Abstractions.Storage.SparkStoreProviders.PostgreSql, global::MintPlayer.Spark.Abstractions.Storage.SparkStoreCapabilities.ChangeNotifications | global::MintPlayer.Spark.Abstractions.Storage.SparkStoreCapabilities.Subscriptions | global::MintPlayer.Spark.Abstractions.Storage.SparkStoreCapabilities.Revisions)]
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

        builder.ReplaceService<Microsoft.EntityFrameworkCore.Query.IQuerySqlGeneratorFactory, SparkQuerySqlGeneratorFactory>();
        builder.ReplaceService<Microsoft.EntityFrameworkCore.Migrations.IHistoryRepository, AdvisoryLockHistoryRepository>();
    }

    /// <summary>
    /// D4: nulls first ascending and last descending, as on SQL Server, InMemory and RavenDB (PostgreSQL's default is the
    /// reverse). Npgsql's generator appends <c>NULLS FIRST</c> / <c>NULLS LAST</c> to every <c>ORDER BY</c> when its
    /// reverse-null-ordering flag is on; the option that sets it went internal in 11.0, so this factory passes the flag.
    /// O20: the generator is <see cref="SparkQuerySqlGenerator"/>.
    /// </summary>
#pragma warning disable EF1001 // Npgsql's generator is pubternal; the conformance cases Nulls_sort_first_ascending_and_last_descending and Substrings_and_column_patterns_ignore_case pin it.
    internal sealed class SparkQuerySqlGeneratorFactory(
        Microsoft.EntityFrameworkCore.Query.QuerySqlGeneratorDependencies dependencies,
        Microsoft.EntityFrameworkCore.Storage.IRelationalTypeMappingSource typeMappingSource,
        Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal.INpgsqlSingletonOptions options)
        : Microsoft.EntityFrameworkCore.Query.IQuerySqlGeneratorFactory
    {
        public Microsoft.EntityFrameworkCore.Query.QuerySqlGenerator Create()
            => new SparkQuerySqlGenerator(dependencies, typeMappingSource, options.PostgresVersion);
    }

    /// <summary>
    /// O20: PostgreSQL refuses a pattern or substring search on a column of <see cref="IgnoreCaseCollation"/>, which is
    /// nondeterministic: version 17 refuses <c>LIKE</c>, <c>ILIKE</c> and <c>strpos</c>, version 18 still <c>ILIKE</c>
    /// (measured, PRD O20). Npgsql renders <c>StartsWith</c>, <c>EndsWith</c> and <c>Contains</c> with a constant or a
    /// captured value as <c>LIKE</c>, and <c>Contains</c> with a column as <c>strpos</c>. So every <c>LIKE</c> and
    /// <c>ILIKE</c> compares its text under the database's deterministic collation (<c>COLLATE "default"</c>) with
    /// <c>ILIKE</c>, and <c>strpos</c> compares lowered text: case is ignored on every column, keys included (ids
    /// compare ignoring case, D7). Equality and <c>left()</c>/<c>right()</c> (a pattern from a column) honour the
    /// collation themselves. The cost: such a <c>LIKE</c> uses no index, which a nondeterministic collation's
    /// <c>LIKE</c> does not either.
    /// </summary>
    internal sealed class SparkQuerySqlGenerator(
        Microsoft.EntityFrameworkCore.Query.QuerySqlGeneratorDependencies dependencies,
        Microsoft.EntityFrameworkCore.Storage.IRelationalTypeMappingSource typeMappingSource,
        Version? postgresVersion)
        : Npgsql.EntityFrameworkCore.PostgreSQL.Query.Internal.NpgsqlQuerySqlGenerator(dependencies, typeMappingSource, reverseNullOrderingEnabled: true, postgresVersion)
    {
        protected override void GenerateLike(Microsoft.EntityFrameworkCore.Query.SqlExpressions.LikeExpression like, bool negated)
            => IgnoringCase(like.Match, like.Pattern, like.EscapeChar, negated);

        protected override System.Linq.Expressions.Expression VisitILike(
            Npgsql.EntityFrameworkCore.PostgreSQL.Query.Expressions.Internal.PgILikeExpression like, bool negated = false)
        {
            IgnoringCase(like.Match, like.Pattern, like.EscapeChar, negated);
            return like;
        }

        private void IgnoringCase(
            Microsoft.EntityFrameworkCore.Query.SqlExpressions.SqlExpression match,
            Microsoft.EntityFrameworkCore.Query.SqlExpressions.SqlExpression pattern,
            Microsoft.EntityFrameworkCore.Query.SqlExpressions.SqlExpression? escape,
            bool negated)
        {
            Sql.Append("(");
            Visit(match);
            Sql.Append(") COLLATE \"default\"");
            Sql.Append(negated ? " NOT ILIKE " : " ILIKE ");
            Visit(pattern);
            if (escape is not null)
            {
                Sql.Append(" ESCAPE ");
                Visit(escape);
            }
        }

        /// <summary>
        /// A string list (<c>text[]</c>, <c>Tags.Contains(x)</c>, which Npgsql renders <c>x = ANY (column)</c>): a collation
        /// on an array column breaks EF's translation of <c>Contains</c>, so the item takes it here. An <c>IN</c> list
        /// (<c>x = ANY (@p)</c>) is left alone: its column's own collation decides, and a key keeps its index.
        /// </summary>
        protected override System.Linq.Expressions.Expression VisitArrayAny(
            Npgsql.EntityFrameworkCore.PostgreSQL.Query.Expressions.Internal.PgAnyExpression any)
        {
            if (any is not { OperatorType: Npgsql.EntityFrameworkCore.PostgreSQL.Query.Expressions.Internal.PgAnyOperatorType.Equal,
                    Array: Microsoft.EntityFrameworkCore.Query.SqlExpressions.ColumnExpression }
                || any.Item.Type != typeof(string))
                return base.VisitArrayAny(any);
            Sql.Append("(");
            Visit(any.Item);
            Sql.Append(") COLLATE ").Append(IgnoreCaseCollation).Append(" = ANY (");
            Visit(any.Array);
            Sql.Append(")");
            return any;
        }

        protected override System.Linq.Expressions.Expression VisitSqlFunction(Microsoft.EntityFrameworkCore.Query.SqlExpressions.SqlFunctionExpression function)
        {
            if (function is not { Name: "strpos", IsBuiltIn: true, Arguments: [var text, var part] })
                return base.VisitSqlFunction(function);
            Sql.Append("strpos(lower((");
            Visit(text);
            Sql.Append(") COLLATE \"default\"), lower(");
            Visit(part);
            Sql.Append("))");
            return function;
        }
    }
#pragma warning restore EF1001

    public override string EfProviderName => "Npgsql.EntityFrameworkCore.PostgreSQL";

    public override string KeyCollation => "C";

    /// <summary>
    /// O20: the collation of an ordinary text column. ICU's root locale at strength 2 (<c>ks-level2</c>) ignores case but
    /// not accents, as RavenDB's lowercasing analyzer does; nondeterministic, so <c>Alpha = ALPHA</c>.
    /// <c>SparkFramework0004</c> creates it, in the schema the app's tables go to.
    /// </summary>
    public const string IgnoreCaseCollation = "spark_ignore_case";

    public override void TextColumn(PropertyBuilder property) => property.UseCollation(IgnoreCaseCollation);

    public override string? IgnoreCaseFrameworkSql
        => $"CREATE COLLATION IF NOT EXISTS {IgnoreCaseCollation} (provider = icu, locale = 'und-u-ks-level2', deterministic = false);";

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
    /// to write a non-UTC value to <c>timestamptz</c>. The base writes its ticks and reads them as UTC (O18). A
    /// <see cref="DateTimeOffset"/> is <c>timestamptz</c>, written as its UTC instant (Npgsql refuses other offsets too);
    /// it reads back with offset 0, the same instant (D5).
    /// </summary>
    public override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        base.ConfigureConventions(builder);
        builder.Properties<DateTime>().HaveColumnType("timestamp without time zone");
        builder.Properties<DateTimeOffset>().HaveConversion<UtcDateTimeOffsetConverter>();
    }

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

    public override string DatabaseSizeSql => "SELECT pg_database_size(current_database())";

    /// <summary>Every ordinary table outside the system schemas, its indexes and TOAST included.</summary>
    public override string TableSizesSql => """
        SELECT c.relname, pg_total_relation_size(c.oid)
        FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE c.relkind IN ('r', 'p') AND n.nspname NOT IN ('pg_catalog', 'information_schema')
        """;

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
