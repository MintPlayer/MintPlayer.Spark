using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Cron;
using MintPlayer.Spark.Moderation.Services;

namespace MintPlayer.Spark.Moderation.Jobs;

/// <summary>
/// Fraud measure 5: credits the ledger entries whose delay has passed and recomputes the recipients'
/// summaries. Crediting is a Cron sweep over a flag written atomically with the vote — not a delayed
/// message per vote — so a withdrawn or reversed vote needs no cancellation.
/// </summary>
internal sealed partial class ModerationCreditingJob : ISparkCronJob
{
    /// <summary>The shipped default; <c>Spark:Moderation:Jobs:CreditingSchedule</c> overrides it.</summary>
    public static string CronSchedule => "*/5 * * * *";

    [Inject] private readonly ReputationLedger ledger;

    public Task RunAsync(CancellationToken cancellationToken) => ledger.CreditDueAsync(cancellationToken: cancellationToken);
}

/// <summary>Fraud measure 6: the nightly detector.</summary>
internal sealed partial class ModerationFraudDetectorJob : ISparkCronJob
{
    /// <summary>The shipped default; <c>Spark:Moderation:Jobs:DetectorSchedule</c> overrides it.</summary>
    public static string CronSchedule => "17 3 * * *";

    [Inject] private readonly FraudDetector detector;

    public Task RunAsync(CancellationToken cancellationToken) => detector.RunAsync(cancellationToken);
}
