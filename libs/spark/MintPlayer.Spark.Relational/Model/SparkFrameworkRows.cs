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

/// <summary>A compare-and-swap value (S15, <c>ISparkAtomicValues</c>): table <c>SparkAtomicValues</c>.</summary>
public sealed class SparkAtomicValueRow
{
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public long Version { get; set; }
}
