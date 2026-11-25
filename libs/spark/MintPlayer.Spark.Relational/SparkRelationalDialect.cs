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

    /// <summary>Type-wide column conventions: decimal precision, date and time types (S12).</summary>
    public virtual void ConfigureConventions(ModelConfigurationBuilder builder) { }

    /// <summary>Makes <paramref name="property"/> a JSON column, written by its converter.</summary>
    public virtual void JsonColumn(PropertyBuilder property) { }

    /// <summary>Classifies a database exception, or <see langword="null"/> when it is no constraint error.</summary>
    public abstract SparkConstraintError? Classify(Exception databaseException);

    /// <summary>
    /// <c>SELECT</c> that locks one row until the transaction ends (S6 <c>PatchAppend</c>): <paramref name="table"/> and
    /// <paramref name="column"/> are delimited identifiers from the model, the key is parameter <c>{0}</c>.
    /// </summary>
    public abstract string LockRowSql(string table, string column);
}
