namespace MintPlayer.Spark.Migrations;

/// <summary>
/// Holds the registered migrations. Shared (as a singleton) between the configuration-time
/// registration API and the startup runner — the same object instance, mirroring the cron registry.
/// </summary>
public sealed class SparkMigrationRegistry
{
    private readonly List<SparkMigrationDescriptor> migrations = [];
    private readonly object gate = new();

    /// <summary>The registered migrations, ascending by <see cref="SparkMigrationDescriptor.Version"/>.</summary>
    public IReadOnlyList<SparkMigrationDescriptor> Migrations
    {
        get { lock (gate) return migrations.OrderBy(m => m.Version).ToArray(); }
    }

    internal void Add(SparkMigrationDescriptor descriptor)
    {
        lock (gate)
        {
            var clash = migrations.FirstOrDefault(m => m.Version == descriptor.Version);
            if (clash is not null)
                throw new InvalidOperationException(
                    $"Two migrations share version {descriptor.Version}: '{clash.Name}' and '{descriptor.Name}'. " +
                    "Migration versions must be unique.");

            migrations.Add(descriptor);
        }
    }
}
