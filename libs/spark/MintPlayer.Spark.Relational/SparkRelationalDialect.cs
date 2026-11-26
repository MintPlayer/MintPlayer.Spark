using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MintPlayer.Spark.Relational;

/// <summary>A constraint the database reported broken, as a dialect classifies it (#536 D30).</summary>
public enum SparkConstraintKind { Unique, ForeignKey, NotNull, Length }

/// <summary>Where a context's migrations live and which history table records them (#536 D33).</summary>
/// <param name="Assembly">The assembly holding the migrations; <see langword="null"/> for the context's own.</param>
/// <param name="HistoryTable">The history table.</param>
/// <param name="HistorySchema">Its schema; <see langword="null"/> for the database's default.</param>
public sealed record SparkMigrationsSettings(string? Assembly, string HistoryTable, string? HistorySchema);

/// <summary>A dialect's reading of a database error: the kind, and the names it carries (constraint, index or column).</summary>
public sealed record SparkConstraintError(SparkConstraintKind Kind, IReadOnlyList<string> Names);

/// <summary>
/// What differs between SQL Server and PostgreSQL (#536 D2): the EF provider, the binary key collation (D7), JSON
/// columns, and the database's error codes. Everything else is <c>MintPlayer.Spark.Relational</c>'s.
/// </summary>
public abstract class SparkRelationalDialect
{
    /// <summary><see cref="Abstractions.Storage.ISparkStore.ProviderName"/>.</summary>
    public abstract string ProviderName { get; }

    /// <summary>
    /// Configures the EF provider for <paramref name="connectionString"/>, with the migrations of
    /// <paramref name="migrations"/> (D33): the assembly that holds them and the history table that records them.
    /// </summary>
    public abstract void UseProvider(DbContextOptionsBuilder builder, string connectionString, SparkMigrationsSettings migrations);

    /// <summary>The EF provider's name, as a migration sees it in <c>MigrationBuilder.ActiveProvider</c>.</summary>
    public abstract string EfProviderName { get; }

    /// <summary>The collation of key and FK text columns: byte comparison, since ids are lowercase by construction (D7).</summary>
    public abstract string KeyCollation { get; }

    /// <summary>
    /// Spark's migration guard (D33, S19): a session-level lock in the server's maintenance database keyed
    /// <c>spark:migrations:{database}</c>, held across the database's creation, the framework schema, the app's
    /// migrations and the data migrations. The database's own migration lock covers neither the creation nor the data
    /// migrations. Dispose the result to release it.
    /// </summary>
    public abstract Task<IAsyncDisposable> AcquireMigrationGuardAsync(string connectionString, CancellationToken cancellationToken);

    /// <summary>The dialects in use, by <see cref="EfProviderName"/>, for the framework migrations (which EF constructs itself).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SparkRelationalDialect> inUse = new(StringComparer.Ordinal);

    internal static void Register(SparkRelationalDialect dialect) => inUse.TryAdd(dialect.EfProviderName, dialect);

    internal static SparkRelationalDialect ForProvider(string? efProviderName)
        => efProviderName is not null && inUse.TryGetValue(efProviderName, out var dialect)
            ? dialect
            : throw new InvalidOperationException($"No Spark dialect is registered for the EF provider '{efProviderName}'.");

    /// <summary>
    /// Type-wide column conventions: decimal precision, date and time types (S12). The base reads every
    /// <see cref="DateTime"/> as UTC (#536 O18); a dialect that overrides this calls it first.
    /// </summary>
    public virtual void ConfigureConventions(ModelConfigurationBuilder builder)
        => builder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();

    /// <summary>
    /// A column keeps a <see cref="DateTime"/>'s ticks, not its kind (#536 O18). Spark and its libraries write
    /// <c>DateTime.UtcNow</c>, so a value reads back as UTC; a local value is written as its UTC instant, and an
    /// unspecified one keeps its ticks. The ticks go to the database unspecified: Npgsql refuses a UTC value for
    /// <c>timestamp without time zone</c>.
    /// </summary>
    private sealed class UtcDateTimeConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>(
        value => DateTime.SpecifyKind(value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value, DateTimeKind.Unspecified),
        value => DateTime.SpecifyKind(value, DateTimeKind.Utc));

    /// <summary>Makes <paramref name="property"/> a JSON column, written by its converter.</summary>
    public virtual void JsonColumn(PropertyBuilder property) { }

    /// <summary>
    /// Shapes an ordinary text column (not a key, not a reference) so that comparisons ignore case, as on RavenDB (#536
    /// O20). SQL Server's ordinary columns take the database's collation, case-insensitive by default; PostgreSQL's
    /// default collation respects case, so its dialect sets one that doesn't.
    /// </summary>
    public virtual void TextColumn(PropertyBuilder property) { }

    /// <summary>What the framework migration <c>SparkFramework0004</c> creates for <see cref="TextColumn"/>, or <see langword="null"/>.</summary>
    public virtual string? IgnoreCaseFrameworkSql => null;

    /// <summary>Classifies a database exception, or <see langword="null"/> when it is no constraint error.</summary>
    public abstract SparkConstraintError? Classify(Exception databaseException);

    /// <summary>
    /// <c>SELECT</c> that locks one row until the transaction ends (S6 <c>PatchAppend</c>): <paramref name="table"/> and
    /// <paramref name="column"/> are delimited identifiers from the model, the key is parameter <c>{0}</c>.
    /// </summary>
    public abstract string LockRowSql(string table, string column);

    /// <summary>
    /// The statement that creates a view (D32): <paramref name="view"/> is the delimited name, <paramref name="select"/>
    /// the SQL EF rendered. It must also run inside the <c>IF … BEGIN … END</c> of an idempotent migration script.
    /// </summary>
    public virtual string CreateViewSql(string view, string select) => $"CREATE VIEW {view} AS\n{select};";

    // ---- D6: the change stamp (#536 M4e) ----

    /// <summary>
    /// The store type of the change stamp (D6): a value the database itself moves on every insert and update, in commit
    /// order below <see cref="HorizonSql"/>. SQL Server's <c>rowversion</c>; PostgreSQL's <c>xid8</c>, set by a trigger.
    /// </summary>
    public abstract string StampColumnType { get; }

    /// <summary>The stamp's default on insert (PostgreSQL's <c>pg_current_xact_id()</c>), or <see langword="null"/>.</summary>
    public virtual string? StampDefaultSql => null;

    /// <summary>Maps <paramref name="property"/> (a <see cref="ulong"/>) as the stamp column: never written by EF.</summary>
    public virtual void StampColumn(PropertyBuilder property)
    {
        property.HasColumnType(StampColumnType).ValueGeneratedOnAddOrUpdate();
        if (StampDefaultSql is { } sql)
            property.HasDefaultValueSql(sql);
    }

    /// <summary>
    /// The statement that makes <paramref name="table"/> (delimited) move its stamp on update, or <see langword="null"/>
    /// when the column type does that itself. Synchronize's migrations emit it after the table or the column is created.
    /// </summary>
    public virtual string? StampTriggerSql(string table) => null;

    /// <summary>What the framework migration that adds the stamp (<c>SparkFramework0002</c>) creates besides the table, or <see langword="null"/>.</summary>
    public virtual string? StampFrameworkSql => null;

    /// <summary>
    /// <c>SELECT</c> of the safe read horizon, as a <c>bigint</c>: every stamp below it belongs to a committed write, and
    /// a write not committed yet gets a stamp at or above it. It moves whenever a transaction commits, so it is also the
    /// cheap probe the feed polls first.
    /// </summary>
    public abstract string HorizonSql { get; }

    /// <summary><c>SELECT</c> of the database's size on disk in bytes, as one <c>bigint</c> (<c>ISparkStoreStatistics</c>).</summary>
    public abstract string DatabaseSizeSql { get; }

    /// <summary>
    /// <c>SELECT</c> of every table's name and size on disk in bytes (<c>bigint</c>), one row per table. Constant text: the
    /// catalog is read, nothing is spliced in.
    /// </summary>
    public abstract string TableSizesSql { get; }
}
