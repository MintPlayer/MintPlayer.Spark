using MintPlayer.Spark.Cron;
using MintPlayer.SourceGenerators.Attributes;

namespace DemoApp.Jobs;

/// <summary>
/// Demonstrates a Spark cron job: writes a heartbeat log line every minute. The schedule lives
/// on the job via the static abstract <see cref="CronSchedule"/> member, and the job is picked up
/// automatically by <c>spark.AddCronJobs()</c>.
/// </summary>
public partial class HeartbeatJob : ISparkCronJob
{
    public static string CronSchedule => "* * * * *"; // every minute (UTC)

    [Inject] private readonly ILogger<HeartbeatJob> logger;

    public Task RunAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Heartbeat cron job ran at {Utc:o}.", DateTime.UtcNow);
        return Task.CompletedTask;
    }
}
