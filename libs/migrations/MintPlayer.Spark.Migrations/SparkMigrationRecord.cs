namespace MintPlayer.Spark.Migrations;

/// <summary>
/// Marker document written once a migration has been applied. Its id is
/// <c>SparkMigrationRecords/{version}</c>, so a migration is applied at most once per database.
/// </summary>
public sealed class SparkMigrationRecord
{
    public string? Id { get; set; }
    public long Version { get; set; }
    public string? Name { get; set; }
    public DateTimeOffset AppliedOnUtc { get; set; }
}
