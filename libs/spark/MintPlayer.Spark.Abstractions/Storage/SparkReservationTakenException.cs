namespace MintPlayer.Spark.Abstractions.Storage;

/// <summary>
/// <see cref="ISparkSession.SaveChangesAsync"/> could not claim <see cref="Key"/> (<see cref="ISparkSession.Reserve"/>):
/// another document holds it. Nothing of the unit of work was written.
/// </summary>
public sealed class SparkReservationTakenException(string key, string owner)
    : Exception($"'{key}' is reserved by another document ('{owner}').")
{
    public string Key { get; } = key;

    /// <summary>The document that holds the key.</summary>
    public string Owner { get; } = owner;
}
