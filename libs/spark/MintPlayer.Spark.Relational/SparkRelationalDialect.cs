using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MintPlayer.Spark.Relational;

/// <summary>A constraint the database reported broken, as a dialect classifies it (#536 D30).</summary>
public enum SparkConstraintKind { Unique, ForeignKey, NotNull, Length }

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

    /// <summary>Configures the EF provider for <paramref name="connectionString"/>.</summary>
    public abstract void UseProvider(DbContextOptionsBuilder builder, string connectionString);

    /// <summary>The collation of key and FK text columns: byte comparison, since ids are lowercase by construction (D7).</summary>
    public abstract string KeyCollation { get; }

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
