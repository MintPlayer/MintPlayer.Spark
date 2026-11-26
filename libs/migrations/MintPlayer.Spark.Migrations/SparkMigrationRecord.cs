using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Migrations;

/// <summary>
/// Marker document written once a migration has been applied. Its id is
/// <c>sparkmigrationrecords/{version}</c>, so a migration is applied at most once per database. Markers written
/// before #536 keep their <c>SparkMigrationRecords/</c> casing; RavenDB ids ignore case, so they still count.
/// </summary>
[IdKind(SparkIdKind.Natural)]
public sealed partial class SparkMigrationRecord
{
    public Id<SparkMigrationRecord> Id { get; set; }
    public long Version { get; set; }
    public string? Name { get; set; }
    public DateTimeOffset AppliedOnUtc { get; set; }
}
