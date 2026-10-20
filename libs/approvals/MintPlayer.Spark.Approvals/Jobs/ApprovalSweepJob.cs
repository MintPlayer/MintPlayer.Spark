using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Approvals.Services;
using MintPlayer.Spark.Cron;

namespace MintPlayer.Spark.Approvals.Jobs;

/// <summary>
/// Expires overdue requests (running each kind's expiry in the request's own guarded commit) and tells the deciders
/// of requests raised inside another session once it committed. Idempotent: two instances racing commit once.
/// </summary>
internal sealed partial class ApprovalSweepJob : ISparkCronJob
{
    /// <summary>The shipped default; <c>Spark:Approvals:SweepSchedule</c> overrides it.</summary>
    public static string CronSchedule => "* * * * *";

    [Inject] private readonly SparkApprovals approvals;

    public Task RunAsync(CancellationToken cancellationToken) => approvals.SweepAsync(cancellationToken);
}
