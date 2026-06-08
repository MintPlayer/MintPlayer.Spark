namespace MintPlayer.Spark.Migrations;

/// <summary>A registered migration: its CLR type, version, display name, and optional description.</summary>
public sealed record SparkMigrationDescriptor(Type MigrationType, long Version, string Name, string? Description);
