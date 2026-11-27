using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.Migrations;

/// <summary>The data migrations as <c>--spark-migrate</c> runs them (#536 D33): the startup run, on demand.</summary>
internal sealed class SparkDataMigrations : ISparkDataMigrations
{
    private readonly IServiceProvider services;

    // The root provider: the runner opens a scope per migration, as it does at startup.
    public SparkDataMigrations(IServiceProvider services) => this.services = services;

    public Task RunAsync(CancellationToken cancellationToken) => SparkMigrationRunner.RunAsync(services, cancellationToken);
}
