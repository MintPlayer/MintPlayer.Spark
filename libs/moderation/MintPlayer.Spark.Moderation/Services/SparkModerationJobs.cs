using MintPlayer.SourceGenerators.Attributes;

namespace MintPlayer.Spark.Moderation.Services;

/// <summary>The on-demand face of the two Cron jobs (<see cref="ISparkModerationJobs"/>).</summary>
internal sealed partial class SparkModerationJobs : ISparkModerationJobs
{
    [Inject] private readonly ReputationLedger ledger;
    [Inject] private readonly FraudDetector detector;

    public Task<int> RunCreditingAsync(CancellationToken cancellationToken = default)
        => ledger.CreditDueAsync(cancellationToken: cancellationToken);

    public Task<FraudDetectionReport> RunFraudDetectorAsync(CancellationToken cancellationToken = default)
        => detector.RunAsync(cancellationToken);

    public Task RecomputeReputationAsync(IEnumerable<string> userIds, CancellationToken cancellationToken = default)
        => ledger.RecomputeAsync(userIds, cancellationToken);
}
