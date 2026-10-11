using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Cron;

namespace SeoTester.Monitoring;

/// <summary>
/// The monitoring sweep (<c>docs/seo_analyzer_PRD.md</c> D12): every 15 minutes, up to 200 due slots a pass.
/// Cluster-safe through Spark cron's claim, and each slot is broadcast once, so even two sweeps start one run.
/// The work is <see cref="MonitoringScheduler.DispatchDueAsync"/>.
/// </summary>
public partial class MonitoringSweepCronJob : ISparkCronJob
{
    [Inject] private readonly MonitoringScheduler scheduler;

    public static string CronSchedule => "*/15 * * * *";

    public Task RunAsync(CancellationToken cancellationToken) => scheduler.DispatchDueAsync(batch: 200, cancellationToken);
}
