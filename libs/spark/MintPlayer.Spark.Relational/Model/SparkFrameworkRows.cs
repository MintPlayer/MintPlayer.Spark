namespace MintPlayer.Spark.Relational.Model;

/// <summary>A unique key held for a document (S6 <c>Reserve</c>): table <c>SparkReservations</c>.</summary>
public sealed class SparkReservationRow
{
    public string Key { get; set; } = string.Empty;

    public string OwnerId { get; set; } = string.Empty;

    public long Version { get; set; }
}

/// <summary>A Data Protection key (<c>Spark:DataProtection:Storage = Store</c>): table <c>SparkDataProtectionKeys</c>.</summary>
public sealed class SparkDataProtectionKeyRow
{
    public string Name { get; set; } = string.Empty;

    public string Xml { get; set; } = string.Empty;
}

/// <summary>
/// A deleted row (#536 D6): table <c>spark.Tombstones</c>, written in the deleting transaction, read by the change feed
/// below the same horizon as the tables, and swept after <see cref="Storage.RelationalSparkChangeFeed.TombstoneRetention"/>.
/// </summary>
public sealed class SparkTombstoneRow
{
    public Guid Id { get; set; }

    /// <summary>The deleted row's collection (its table name).</summary>
    public string Collection { get; set; } = string.Empty;

    /// <summary>The deleted row's id.</summary>
    public string DocumentId { get; set; } = string.Empty;

    /// <summary>The stamp the database gave the deleting write.</summary>
    public ulong Stamp { get; set; }

    public DateTimeOffset DeletedAt { get; set; }
}

/// <summary>
/// One revision of a document (#536 S8, O12): table <c>spark.Revisions</c>, written in the writing transaction. A delete
/// revision has no <see cref="Json"/>.
/// </summary>
public sealed class SparkRevisionRow
{
    public Guid Id { get; set; }

    /// <summary>The document's collection (its table name).</summary>
    public string Collection { get; set; } = string.Empty;

    public string DocumentId { get; set; } = string.Empty;

    /// <summary>The document's version the revision holds (D3); a delete revision's is the deleted version + 1.</summary>
    public long Version { get; set; }

    public DateTimeOffset At { get; set; }

    /// <summary>The CLR type the JSON reads back as (the aggregate root's concrete type).</summary>
    public string? ClrType { get; set; }

    /// <summary>The whole aggregate (row, child rows, reference lists) as Spark serializes a document.</summary>
    public string? Json { get; set; }
}

/// <summary>A compare-and-swap value (S15, <c>ISparkAtomicValues</c>): table <c>SparkAtomicValues</c>.</summary>
public sealed class SparkAtomicValueRow
{
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public long Version { get; set; }
}
