using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Cron;

namespace SparkApp.Jobs;

/// <summary>A sample cron job: it logs once an hour. Every ISparkCronJob in this project is registered by AddCronJobs().</summary>
public partial class HeartbeatCronJob : ISparkCronJob
{
    [Inject] private readonly ILogger<HeartbeatCronJob> logger;

    public static string CronSchedule => "0 * * * *";

    public Task RunAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Heartbeat at {Time}", DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }
}
