using MintPlayer.ValueComparerGenerator.Attributes;
using System.Collections.Generic;

namespace MintPlayer.Spark.SourceGenerators.Models;

[GenerateEquality]
public partial class MigrationClassInfo
{
    public string MigrationTypeName { get; set; } = string.Empty;
}

/// <summary>
/// The migrations that referenced packages ship (#388): public <c>ISparkMigration</c> classes in
/// assemblies that reference <c>MintPlayer.Spark.Migrations</c>. Read only for a host application,
/// since only the app's generated <c>AddMigrations()</c> registers them.
/// </summary>
[GenerateEquality]
public partial class ReferencedMigrationsInfo
{
    public List<MigrationClassInfo> Migrations { get; set; } = new();
}
