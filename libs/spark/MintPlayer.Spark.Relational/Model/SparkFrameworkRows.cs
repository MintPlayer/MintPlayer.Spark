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

/// <summary>A compare-and-swap value (S15, <c>ISparkAtomicValues</c>): table <c>SparkAtomicValues</c>.</summary>
public sealed class SparkAtomicValueRow
{
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public long Version { get; set; }
}
