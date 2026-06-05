using MintPlayer.Spark.Cron;
using MintPlayer.SourceGenerators.Attributes;

namespace Fleet.Jobs;

/// <summary>
/// Demonstrates a cron job in an <c>AddSparkFull</c> app: with no explicit registration call,
/// the AllFeatures generator detects <see cref="ISparkCronJob"/> implementers and wires
/// <c>spark.AddCronJobs()</c> automatically.
/// </summary>
public partial class FleetHeartbeatJob : ISparkCronJob
{
    public static string CronSchedule => "0 * * * *"; // top of every hour (UTC)

    [Inject] private readonly ILogger<FleetHeartbeatJob> logger;

    public Task RunAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Fleet heartbeat cron job ran at {Utc:o}.", DateTime.UtcNow);
        return Task.CompletedTask;
    }
}
